using System.Security.Claims;
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

        app.MapNuGetV2();
        app.MapNuGetV3();
        app.MapAccountEndpoints();
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

    private static Feed ToFeed(FeedSeedOptions seed, TimeProvider time) => new()
    {
        Name = seed.Name,
        NameLower = seed.Name.ToLowerInvariant(),
        Kind = seed.Kind,
        AnonymousRead = seed.AnonymousRead,
        AllowOverwrite = seed.AllowOverwrite,
        DeletionBehavior = seed.DeletionBehavior,
        CreatedUtc = time.GetUtcNow().UtcDateTime,
    };

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
