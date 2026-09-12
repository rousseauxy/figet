using System.Security.Claims;
using FiGet.Core.Connectors;
using FiGet.Core.Entities;
using FiGet.Core.Feeds;
using FiGet.Core.Packages;
using FiGet.Core.Storage;
using FiGet.Core.Stores;
using FiGet.Core.Tokens;
using FiGet.Http;
using FiGet.Persistence;
using FiGet.Persistence.Sqlite;
using FiGet.Persistence.SqlServer;
using FiGet.Protocol.V2;
using FiGet.Protocol.V3;
using FiGet.Storage;
using FiGet.Web.Components;
using FiGet.Web.Configuration;
using FiGet.Web.Theming;
using NuGet.Versioning;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace FiGet.Web;

/// <summary>Composition root. Program.cs and the integration tests both build the app through here.</summary>
public static class FiGetApp
{
    public const string AdminPolicy = "Admin";
    public const string TokenKeyClaim = "figet:token";

    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddOptions<FiGetOptions>().BindConfiguration(FiGetOptions.SectionName);

        // Everything below resolves options lazily, so settings added after this call (test hosts, later
        // configuration sources) are honoured.
        services.AddOptions<PublicUrlOptions>().Configure<IOptions<FiGetOptions>>((urls, figet) => urls.PublicBaseUrl = figet.Value.PublicBaseUrl);
        services.AddOptions<UploadOptions>().Configure<IOptions<FiGetOptions>>((upload, figet) =>
        {
            upload.MaxPackageSizeBytes = figet.Value.Limits.MaxPackageSizeMB * 1024L * 1024L;
            upload.TempPath = figet.Value.Storage.TempPath;
        });
        services.AddSingleton<StoragePaths>();

        services.AddDbContext<FiGetDbContext>((provider, db) =>
        {
            var options = provider.GetRequiredService<IOptions<FiGetOptions>>().Value;
            var connectionString = provider.GetRequiredService<StoragePaths>().ConnectionString;
            if (options.Database.Provider == DatabaseProvider.SqlServer)
            {
                db.UseFiGetSqlServer(connectionString);
            }
            else
            {
                db.UseFiGetSqlite(connectionString);
            }
        });

        services.AddFiGetStores();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPackageIndexer, PackageIndexer>();
        services.AddSingleton<IPackageStorage>(provider => new FileSystemPackageStorage(Path.Combine(provider.GetRequiredService<StoragePaths>().Root, "files")));
        services.AddScoped<PackageIngestionService>();
        services.AddScoped<AccessTokenService>();

        // Proxy feeds. The client is a singleton because NuGet's repositories cache resources and
        // connections inside themselves; the service is scoped because it writes through the request's
        // database context.
        services.AddSingleton(provider =>
        {
            var connector = provider.GetRequiredService<IOptions<FiGetOptions>>().Value.Connector;
            return new ConnectorSettings
            {
                UpstreamIndexTtl = connector.UpstreamIndexTtl,
                UpstreamTimeout = connector.UpstreamTimeout,
            };
        });
        services.AddSingleton<IUpstreamClient, NuGetUpstreamClient>();
        services.AddScoped<ConnectorService>();

        services.AddDataProtection()
            .SetApplicationName("FiGet")
            .PersistKeysToDbContext<FiGetDbContext>();

        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookie =>
            {
                cookie.LoginPath = "/account/login";
                cookie.LogoutPath = "/account/logout";
                cookie.AccessDeniedPath = "/account/login";
                cookie.Cookie.Name = "figet.auth";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.SlidingExpiration = true;
                cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
                cookie.Events.OnValidatePrincipal = ValidateCookieAsync;
            });
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy.RequireAuthenticatedUser().RequireClaim(ClaimTypes.Role, "admin"));
        services.AddCascadingAuthenticationState();
        services.AddAntiforgery();

        services.AddSingleton<IThemeService, ThemeService>();
        services.AddRazorComponents();
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

        if (builder.Configuration.GetValue<bool>("FiGet:Logging:Json") || builder.Configuration.GetValue<bool>("DOTNET_RUNNING_IN_CONTAINER"))
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole(json => json.UseUtcTimestamp = true);
        }

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService("figet"))
                .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddOtlpExporter())
                .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddRuntimeInstrumentationIfAvailable().AddOtlpExporter());
        }
    }

    public static async Task<WebApplication> BuildAsync(WebApplicationBuilder builder)
    {
        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/error", createScopeForErrors: true);
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

        // A theme pack is a small stylesheet of token overrides, layered after app.css.
        app.MapGet("/themes/{name}.css", (string name, HttpContext http, IThemeService themes) =>
        {
            var theme = themes.GetCss(name);
            if (theme is null)
            {
                return Results.NotFound();
            }

            http.Response.Headers.ETag = theme.Value.ETag;
            http.Response.Headers.CacheControl = "no-cache";
            return Results.Text(theme.Value.Css, "text/css");
        });

        app.MapNuGetV2();
        app.MapNuGetV3();
        app.MapAccountEndpoints();
        app.MapAdminEndpoints();
        app.MapStaticAssets();
        app.MapRazorComponents<App>();

        await InitializeAsync(app);
        return app;
    }

    /// <summary>Migrates the database, seeds configured feeds and makes sure an admin token exists.</summary>
    public static async Task InitializeAsync(WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<FiGetOptions>>().Value;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FiGet.Startup");
        if (options.Database.Provider == DatabaseProvider.Sqlite && options.Database.ExpectedReplicas > 1)
        {
            throw new InvalidOperationException(
                "FiGet:Database:Provider is Sqlite but FiGet:Database:ExpectedReplicas is above 1. SQLite cannot be shared by several instances; use SqlServer.");
        }

        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<FiGetDbContext>();

        if (options.Database.Provider == DatabaseProvider.Sqlite)
        {
            var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
            var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        if (options.Database.MigrateOnStartup)
        {
            await db.Database.MigrateAsync();
        }

        if (options.Database.Provider == DatabaseProvider.Sqlite)
        {
            // Write-ahead logging lets readers continue while a push is being written.
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        }

        var feeds = services.GetRequiredService<IFeedStore>();
        var time = services.GetRequiredService<TimeProvider>();
        foreach (var seed in options.Feeds)
        {
            if (!FeedNames.IsValid(seed.Name))
            {
                throw new InvalidOperationException($"Configured feed name '{seed.Name}' is not valid.");
            }

            if (await feeds.CreateAsync(ToFeed(seed, time), CancellationToken.None))
            {
                logger.LogInformation("Created feed {Feed} from configuration.", seed.Name);
            }
        }

        if ((await feeds.ListAsync(CancellationToken.None)).Count == 0
            && await feeds.CreateAsync(ToFeed(new FeedSeedOptions { Name = "default" }, time), CancellationToken.None))
        {
            logger.LogInformation("No feeds configured; created feed 'default'.");
        }

        var tokens = services.GetRequiredService<AccessTokenService>();
        if (!string.IsNullOrWhiteSpace(options.Auth.BootstrapAdminToken))
        {
            try
            {
                await tokens.EnsureAsync("bootstrap", options.Auth.BootstrapAdminToken, TokenScopes.Admin, CancellationToken.None);
            }
            catch (DbUpdateException)
            {
                // Another replica registered it at the same moment.
            }
        }
        else if (!await services.GetRequiredService<IAccessTokenStore>().AnyActiveAdminAsync(time.GetUtcNow().UtcDateTime, CancellationToken.None))
        {
            var created = await tokens.CreateAsync("bootstrap", TokenScopes.Admin, feedKey: null, expiresUtc: null, CancellationToken.None);
            logger.LogWarning(
                "No admin token existed, so one was generated. It is shown only this once; store it now: {BootstrapAdminToken}",
                created.Secret);
        }
    }

    private static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapPost("/account/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/account/login");
        });
    }

    /// <summary>
    /// The buttons of the admin pages. Plain form posts rather than interactive components, because these
    /// pages are statically rendered; every one of them changes something, so all are admin-only and all
    /// carry an antiforgery token.
    /// </summary>
    private static void MapAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/admin").RequireAuthorization(AdminPolicy);

        // Fetch a package an upstream has but this feed has not cached yet.
        admin.MapPost("/feeds/{feed}/pull", async (
            string feed,
            HttpContext http,
            IFeedStore feeds,
            ConnectorService connector,
            CancellationToken cancellationToken) =>
        {
            var form = await http.Request.ReadFormAsync(cancellationToken);
            var target = await feeds.FindAsync(feed, cancellationToken);
            if (target is not null && NuGetVersion.TryParse(form["version"].ToString(), out var version))
            {
                await connector.EnsureCachedAsync(target, form["id"].ToString(), version, cancellationToken);
            }

            return Back(form["returnUrl"].ToString(), $"/feeds/{Uri.EscapeDataString(feed)}");
        });

        admin.MapPost("/feeds/{feed}/upstreams/add", async (
            string feed,
            HttpContext http,
            IFeedStore feeds,
            CancellationToken cancellationToken) =>
        {
            var form = await http.Request.ReadFormAsync(cancellationToken);
            var target = await feeds.FindAsync(feed, cancellationToken);
            var name = form["name"].ToString().Trim();
            var url = form["url"].ToString().Trim();
            if (target is not null && name.Length > 0 && url.Length > 0)
            {
                await feeds.AddUpstreamAsync(
                    target.Key,
                    new FeedUpstream
                    {
                        Name = name,
                        Url = url,
                        Kind = form["kind"].ToString().Equals("V2", StringComparison.OrdinalIgnoreCase) ? UpstreamKind.V2 : UpstreamKind.V3,
                        // Stored as the browser sent them: the pattern reader splits on the separator and
                        // trims each line, so the carriage returns a textarea adds are already harmless.
                        Allow = form["allow"].ToString().Trim(),
                        Deny = form["deny"].ToString().Trim(),
                        CredentialRef = form["credentialRef"].ToString().Trim() is { Length: > 0 } reference ? reference : null,
                    },
                    cancellationToken);
            }

            return Back(form["returnUrl"].ToString(), $"/feeds/{Uri.EscapeDataString(feed)}/settings");
        });

        admin.MapPost("/feeds/{feed}/upstreams/remove", async (
            string feed,
            HttpContext http,
            IFeedStore feeds,
            CancellationToken cancellationToken) =>
        {
            var form = await http.Request.ReadFormAsync(cancellationToken);
            var target = await feeds.FindAsync(feed, cancellationToken);
            if (target is not null && int.TryParse(form["key"].ToString(), out var upstreamKey))
            {
                await feeds.RemoveUpstreamAsync(target.Key, upstreamKey, cancellationToken);
            }

            return Back(form["returnUrl"].ToString(), $"/feeds/{Uri.EscapeDataString(feed)}/settings");
        });
    }

    /// <summary>Back where the button was pressed, as long as that is a page on this server.</summary>
    private static IResult Back(string? returnUrl, string fallback) =>
        !string.IsNullOrWhiteSpace(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            ? Results.Redirect(returnUrl)
            : Results.Redirect(fallback);

    private static async Task ValidateCookieAsync(CookieValidatePrincipalContext context)
    {
        var claim = context.Principal?.FindFirst(TokenKeyClaim)?.Value;
        var store = context.HttpContext.RequestServices.GetRequiredService<IAccessTokenStore>();
        var time = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
        var token = int.TryParse(claim, out var key) ? await store.FindAsync(key, context.HttpContext.RequestAborted) : null;
        var now = time.GetUtcNow().UtcDateTime;
        if (token is null || token.RevokedUtc is not null || (token.ExpiresUtc is not null && token.ExpiresUtc <= now) || !token.Scopes.HasFlag(TokenScopes.Admin))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    private static Feed ToFeed(FeedSeedOptions seed, TimeProvider time)
    {
        var upstreams = seed.Upstreams
            .Select((upstream, index) => new FeedUpstream
            {
                Ordinal = index,
                Name = string.IsNullOrWhiteSpace(upstream.Name) ? $"upstream-{index + 1}" : upstream.Name,
                Url = upstream.Url,
                Kind = upstream.Kind,
                Allow = string.Join(FeedUpstream.PatternSeparator, upstream.Allow),
                Deny = string.Join(FeedUpstream.PatternSeparator, upstream.Deny),
                CredentialRef = upstream.CredentialRef,
            })
            .ToList();

        return new Feed
        {
            Name = seed.Name,
            NameLower = seed.Name.ToLowerInvariant(),
            // A feed that names upstreams is a proxy feed, whatever the configuration says, because that is
            // what it will behave like.
            Kind = upstreams.Count > 0 ? FeedKind.Proxy : seed.Kind,
            AnonymousRead = seed.AnonymousRead,
            AllowOverwrite = seed.AllowOverwrite,
            DeletionBehavior = seed.DeletionBehavior,
            CreatedUtc = time.GetUtcNow().UtcDateTime,
            Upstreams = upstreams,
        };
    }

    private static MeterProviderBuilder AddRuntimeInstrumentationIfAvailable(this MeterProviderBuilder metrics) =>
        metrics.AddMeter("System.Runtime", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel");

    private sealed class DatabaseHealthCheck(FiGetDbContext db) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The database is not reachable.");
    }
}
