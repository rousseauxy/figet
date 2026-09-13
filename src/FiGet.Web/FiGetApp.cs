using System.Globalization;
using System.Security.Claims;
using FiGet.Application.Assets;
using FiGet.Application.Connectors;
using FiGet.Application.Packages;
using FiGet.Application.Ports;
using FiGet.Application.Tokens;
using FiGet.Domain.Entities;
using FiGet.Domain.Assets;
using FiGet.Domain.Feeds;
using FiGet.Http;
using FiGet.Infrastructure.Assets;
using FiGet.Infrastructure.Packages;
using FiGet.Infrastructure.Persistence;
using FiGet.Infrastructure.SqlServer;
using FiGet.Infrastructure.Sqlite;
using FiGet.Infrastructure.Storage;
using FiGet.Infrastructure.Upstream;
using FiGet.Infrastructure;
using FiGet.Protocol.Assets;
using FiGet.Protocol.Management;
using FiGet.Protocol.V2;
using FiGet.Protocol.V3;
using FiGet.Web.Components;
using FiGet.Web.Configuration;
using FiGet.Web.Connectors;
using FiGet.Web.Logging;
using FiGet.Web.Theming;
using NuGet.Versioning;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.ResponseCompression;
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
            upload.MaxAssetSizeBytes = figet.Value.Limits.MaxAssetSizeMB * 1024L * 1024L;
            upload.MaxImportSizeBytes = figet.Value.Limits.MaxImportSizeMB * 1024L * 1024L;
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
        services.AddSingleton<IAssetStorage>(provider => new FileSystemAssetStorage(Path.Combine(provider.GetRequiredService<StoragePaths>().Root, "files")));
        services.AddScoped<PackageIngestionService>();
        services.AddScoped<AssetService>();
        services.AddScoped<AssetArchiveService>();
        services.AddSingleton<IRemoteFileSource>(provider =>
        {
            var fetch = provider.GetRequiredService<IOptions<FiGetOptions>>().Value.Assets.RemoteFetch;
            return new HttpRemoteFileSource(new RemoteFetchSettings { Timeout = fetch.Timeout, AllowPrivateNetworks = fetch.AllowPrivateNetworks });
        });
        services.AddHostedService<AssetUploadCleanupService>();
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
        services.AddSingleton(sp => new UpstreamMetadataCache(
            sp.GetRequiredService<IOptions<FiGetOptions>>().Value.Connector.MaxDescribedPackages));

        // Refreshing a stale catalogue happens behind the request that noticed it was stale. The queue is
        // shared, the worker is one loop, and the connector only ever asks - it never waits.
        services.AddSingleton<UpstreamRefreshQueue>();
        services.AddSingleton<IUpstreamRefreshQueue>(sp => sp.GetRequiredService<UpstreamRefreshQueue>());
        services.AddHostedService<UpstreamRefreshService>();
        services.AddScoped<ConnectorService>();
        services.AddScoped<DependencyPuller>();

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
        // Interactive rendering exists for the signed-in views only; every public page stays statically
        // rendered and ships no framework. A circuit is server state held per connected reader, and the
        // public surface is reachable without credentials, so it is not somewhere to allocate it.
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
        services.AddResponseCompression(compression =>
        {
            // Over HTTPS too, which the framework leaves off because of BREACH: that attack needs a secret and
            // attacker-chosen text in the same compressed body. A protocol answer carries neither - no token is
            // ever echoed - and these are the only paths compressed.
            compression.EnableForHttps = true;
            compression.Providers.Add<BrotliCompressionProvider>();
            compression.Providers.Add<GzipCompressionProvider>();

            // Text only. A nupkg or a symbol file is already a zip, and compressing it again costs CPU for nothing.
            compression.MimeTypes = ["application/atom+xml", "application/xml", "application/json", "text/plain"];
        });

        // Fastest, not smallest: the measured cost of a big listing is time, and the fastest level already removes
        // most of the size - repeated tags are exactly what any level compresses well.
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
        services.AddSingleton<AuditLog>();

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

        // First, so it wraps everything written for these paths. Browser pages are not compressed here: they are
        // small, and the interactive runtime's own traffic is not HTTP bodies.
        if (app.Services.GetRequiredService<IOptions<FiGetOptions>>().Value.CompressProtocolResponses)
        {
            app.UseWhen(
                context => context.Request.Path.StartsWithSegments("/nuget", StringComparison.OrdinalIgnoreCase)
                    || context.Request.Path.StartsWithSegments("/api/packages", StringComparison.OrdinalIgnoreCase),
                protocol => protocol.UseResponseCompression());
        }

        // Errors a person meets in a browser get a page with the code on it, as in CustomsHive: an unhandled
        // exception renders /error, and a response that ends with an error status and no body is re-executed to
        // /error/{code}. That includes a page that finds nothing - an unknown feed, package or asset directory - and
        // sets 404 while rendering: .NET 10 drops such a page's own markup, so it would otherwise be a blank page.
        //
        // Protocol paths are left alone. A NuGet client, the reference client or a script reads FiGet's own status and body, and
        // several of those answers are deliberately empty - the api/v2 probe's 404 among them - so an HTML page in
        // their place would change what a client receives. An exception there is a plain-text 500.
        app.UseWhen(context => !IsProtocolPath(context.Request.Path), browser =>
        {
            browser.UseExceptionHandler("/error", createScopeForErrors: true);
            browser.UseStatusCodePagesWithReExecute("/error/{0}", createScopeForStatusCodePages: true);
        });
        app.UseWhen(context => IsProtocolPath(context.Request.Path), protocol =>
            protocol.UseExceptionHandler(new ExceptionHandlerOptions
            {
                ExceptionHandler = async context =>
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    context.Response.ContentType = "text/plain; charset=utf-8";
                    await context.Response.WriteAsync($"The server failed while handling the request (request {context.TraceIdentifier}).");
                },
            }));

        // Explicit, and after the error handling: both re-run the pipeline for /error, which needs routing to run
        // again after them rather than having happened once before them.
        app.UseRouting();

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        // After authentication on purpose: the line says who the caller turned out to be, not just where
        // it came from. Opt-in, because a package client is chatty and most instances never need it.
        if (app.Configuration.GetValue<bool>("FiGet:Logging:Requests"))
        {
            app.UseMiddleware<RequestLogMiddleware>();
        }

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
        app.MapAssetEndpoints();
        app.MapPackageManagement();
        app.MapAccountEndpoints();
        app.MapAdminEndpoints();
        app.MapStaticAssets();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

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
        // Every change here needs the antiforgery token of one of this server's own pages. Checked explicitly
        // because the framework's own check is not what it looks like: the antiforgery middleware only
        // records a verdict, and minimal APIs enforce it only while binding a form to a parameter. These
        // handlers read their forms by hand, so every one of them accepted a post from any page the admin
        // had open - and SameSite=Lax does not stop that from a sibling subdomain, which counts as the same
        // site. The token is read from the RequestVerificationToken header or the form, so the upload page,
        // whose body is the file, is covered by the same check.
        var admin = app.MapGroup("/admin")
            .RequireAuthorization(AdminPolicy)
            .AddEndpointFilter(async (context, next) =>
            {
                var http = context.HttpContext;
                if (!HttpMethods.IsGet(http.Request.Method)
                    && !HttpMethods.IsHead(http.Request.Method)
                    && !await http.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(http))
                {
                    return Results.Json(new { error = "The page has expired. Reload it and try again." }, statusCode: StatusCodes.Status400BadRequest);
                }

                return await next(context);
            });

        // The bare path is what a person types when they want the admin area, and it used to answer 404.
        // Inside the authorised group on purpose: a stranger then meets the sign-in page, rather than a
        // redirect to a page that would only bounce them to the sign-in page anyway. One template only:
        // "" and "/" both normalise to the group prefix, so mapping both made every request to it an
        // AmbiguousMatchException, which surfaces as a 500 rather than as anything that names the cause.
        admin.MapGet("", () => Results.Redirect("/admin/feeds"));

        // Fetch a package an upstream has but this feed has not cached yet, with everything it depends on: the
        // machine a pull is for has no internet, and a meta-module without its sub-modules does not install.
        admin.MapPost("/feeds/{feed}/pull", async (
            string feed,
            HttpContext http,
            IFeedStore feeds,
            DependencyPuller puller,
            AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var form = await http.Request.ReadFormAsync(cancellationToken);
            var target = await feeds.FindAsync(feed, cancellationToken);
            var returnUrl = form["returnUrl"].ToString();
            if (target is not null && NuGetVersion.TryParse(form["version"].ToString(), out var version))
            {
                var report = await puller.PullAsync(target, form["id"].ToString(), version, cancellationToken);
                audit.Record(
                    http,
                    "package.pull",
                    form["id"].ToString(),
                    $"feed={feed} version={version.ToNormalizedString()} fetched={report.Fetched.Count} present={report.AlreadyHere.Count} unavailable={report.Unavailable.Count} capped={report.StoppedAtLimit}");
                returnUrl = WithPullResult(returnUrl, report);
            }

            return Back(returnUrl, $"/feeds/{Uri.EscapeDataString(feed)}");
        });

        admin.MapPost("/feeds/{feed}/upstreams/add", async (
            string feed,
            HttpContext http,
            IFeedStore feeds,
            AuditLog audit,
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

                // The url as well as the name: pointing a feed at a different gallery is the change worth
                // being able to find afterwards.
                audit.Record(http, "upstream.add", name, $"feed={feed} url={url} kind={form["kind"]}");
            }

            return Back(form["returnUrl"].ToString(), $"/admin/feeds/{Uri.EscapeDataString(feed)}");
        });

        // The two buttons of the unlisted view. Relisting offers a version again; deleting removes it for
        // good, because unlisting what is already unlisted would do nothing.
        admin.MapPost("/feeds/{feed}/versions/relist", async (
            string feed,
            HttpContext http,
            IFeedStore feeds,
            PackageIngestionService ingestion,
            AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var form = await http.Request.ReadFormAsync(cancellationToken);
            var target = await feeds.FindAsync(feed, cancellationToken);
            if (target is not null && await ingestion.RelistAsync(target, form["id"].ToString(), form["version"].ToString(), cancellationToken))
            {
                audit.Record(http, "package.relist", form["id"].ToString(), $"feed={feed} version={form["version"]}");
            }

            return Back(form["returnUrl"].ToString(), $"/admin/feeds/{Uri.EscapeDataString(feed)}/unlisted");
        });

        admin.MapPost("/feeds/{feed}/versions/delete", async (
            string feed,
            HttpContext http,
            IFeedStore feeds,
            PackageIngestionService ingestion,
            AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var form = await http.Request.ReadFormAsync(cancellationToken);
            var target = await feeds.FindAsync(feed, cancellationToken);
            if (target is not null && await ingestion.PurgeAsync(target, form["id"].ToString(), form["version"].ToString(), cancellationToken))
            {
                audit.Record(http, "package.delete", form["id"].ToString(), $"feed={feed} version={form["version"]}");
            }

            return Back(form["returnUrl"].ToString(), $"/admin/feeds/{Uri.EscapeDataString(feed)}/unlisted");
        });

        // Forget what this feed cached of one package, so it follows the gallery again. Not a delete: the
        // versions come back on the next download, which is why one button does it rather than a typed
        // confirmation.
        admin.MapPost("/feeds/{feed}/packages/uncache", async (
            string feed,
            HttpContext http,
            IFeedStore feeds,
            PackageIngestionService ingestion,
            AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var form = await http.Request.ReadFormAsync(cancellationToken);
            var target = await feeds.FindAsync(feed, cancellationToken);
            var id = form["id"].ToString();
            if (target is not null && id.Length > 0)
            {
                // The count, because the number on the button is a snapshot from when the page rendered
                // and what actually went is the thing worth recording.
                var removed = await ingestion.UncacheAsync(target, id, cancellationToken);
                audit.Record(http, "package.uncache", id, $"feed={feed} removed={removed}");
            }

            return Back(form["returnUrl"].ToString(), $"/feeds/{Uri.EscapeDataString(feed)}");
        });

        // The upload page. Its own route rather than the API's, because the API accepts tokens and never the
        // sign-in cookie: a cookie is sent by the browser on its own, so an API that honoured one could be
        // made to upload by any page the admin happened to visit. Here the request carries the antiforgery
        // token in a header - the body is the file - and the storing is the API's own code.
        admin.MapPost("/assets/{directory}/upload", async (
            string directory,
            string? path,
            bool? overwrite,
            HttpContext http,
            IFeedStore feeds,
            AssetService assets,
            IOptions<UploadOptions> upload,
            AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var target = await feeds.FindAsync(directory, cancellationToken);
            if (target is not { Kind: FeedKind.Assets } || !AssetPath.TryParse(path, out var parsed) || parsed.IsRoot)
            {
                return Results.Json(new { error = "There is no such asset directory, or the file name is not valid." }, statusCode: StatusCodes.Status400BadRequest);
            }

            var outcome = await AssetEndpoints.WriteAsync(
                http,
                target,
                parsed,
                http.Request.Body,
                http.Request.ContentType,
                overwrite == true ? AssetWriteMode.CreateOrReplace : AssetWriteMode.CreateOnly,
                assets,
                upload.Value,
                cancellationToken);
            if (outcome is AssetOutcome.Created or AssetOutcome.Replaced)
            {
                audit.Record(http, "asset.upload", parsed.Value, $"directory={target.Name} outcome={outcome}");
            }

            return outcome switch
            {
                AssetOutcome.Created or AssetOutcome.Replaced => Results.Json(new { outcome = outcome.ToString() }, statusCode: StatusCodes.Status201Created),
                AssetOutcome.AlreadyExists => Results.Json(new { error = "A file with this name already exists." }, statusCode: StatusCodes.Status409Conflict),
                AssetOutcome.TooLarge => Results.Json(new { error = "The file is larger than this server accepts." }, statusCode: StatusCodes.Status413PayloadTooLarge),
                AssetOutcome.WrongType => Results.Json(new { error = "A folder with this name already exists." }, statusCode: StatusCodes.Status409Conflict),
                _ => Results.Json(new { error = "The file could not be stored here." }, statusCode: StatusCodes.Status400BadRequest),
            };
        }).DisableAntiforgery();

        // Importing an archive from the page: the body is the archive and the token travels in a header, for the
        // same reason as the upload above. The import itself is the API's.
        admin.MapPost("/assets/{directory}/import", async (
            string directory,
            string? path,
            string? format,
            bool? overwrite,
            HttpContext http,
            IFeedStore feeds,
            AssetArchiveService archives,
            IOptions<UploadOptions> upload,
            AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var target = await feeds.FindAsync(directory, cancellationToken);
            if (target is not { Kind: FeedKind.Assets } || !AssetPath.TryParse(path, out var folder) || AssetEndpoints.ParseFormat(format) is not { } parsed)
            {
                return Results.Json(new { error = "There is no such asset directory, or the archive is not a .zip or .tar.gz." }, statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await AssetEndpoints.ImportAsync(http, target, folder, parsed, overwrite == true, archives, upload.Value, cancellationToken);
            audit.Record(http, "asset.import", folder.IsRoot ? "/" : folder.Value, $"directory={target.Name} imported={result.Imported} skipped={result.Skipped} failed={result.Failed.Count}");
            return AssetEndpoints.ImportResult(result);
        }).DisableAntiforgery();

        // Fetching a file from a URL. A plain form post that waits for the download, then returns to the
        // folder with a word on how it went - a fixed code, so the page never echoes text from the request.
        admin.MapPost("/assets/{directory}/fetch", async (
            string directory,
            HttpContext http,
            IFeedStore feeds,
            AssetService assets,
            IRemoteFileSource remote,
            IOptions<UploadOptions> upload,
            AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var form = await http.Request.ReadFormAsync(cancellationToken);
            var target = await feeds.FindAsync(directory, cancellationToken);
            var returnUrl = form["returnUrl"].ToString();
            var fallback = $"/assets/{Uri.EscapeDataString(directory)}";
            var url = form["url"].ToString().Trim();
            var name = form["name"].ToString().Trim();
            if (name.Length == 0 && Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl))
            {
                name = Uri.UnescapeDataString(parsedUrl.Segments.LastOrDefault()?.TrimEnd('/') ?? "");
            }

            if (target is not { Kind: FeedKind.Assets }
                || !AssetPath.TryParse(form["parent"].ToString(), out var parent)
                || name.Length == 0
                || name.Contains('/', StringComparison.Ordinal)
                || !AssetPath.TryParse(parent.IsRoot ? name : parent.Value + "/" + name, out var path))
            {
                return Back(WithResult(returnUrl, "invalid"), fallback);
            }

            var (outcome, _) = await AssetEndpoints.FetchAsync(
                target,
                path,
                url,
                form["overwrite"].ToString() is "true" or "on" ? AssetWriteMode.CreateOrReplace : AssetWriteMode.CreateOnly,
                assets,
                remote,
                upload.Value,
                cancellationToken);
            if (outcome is AssetOutcome.Created or AssetOutcome.Replaced)
            {
                audit.Record(http, "asset.fetch", path.Value, $"directory={target.Name} url={url} outcome={outcome}");
            }

            var code = outcome switch
            {
                AssetOutcome.Created or AssetOutcome.Replaced => "fetched",
                AssetOutcome.AlreadyExists => "exists",
                AssetOutcome.TooLarge => "too-large",
                AssetOutcome.InvalidPath => "refused",
                _ => "failed",
            };
            return Back(WithResult(returnUrl, code), fallback);
        });

        // Downloading a folder as an archive from the page. A GET, so the sign-in cookie is enough: reading a
        // folder changes nothing, and the API would want a token the browser does not have.
        admin.MapGet("/assets/{directory}/export", async (
            string directory,
            string? path,
            string? format,
            IFeedStore feeds,
            AssetArchiveService archives,
            IOptions<UploadOptions> upload,
            CancellationToken cancellationToken) =>
        {
            var target = await feeds.FindAsync(directory, cancellationToken);
            return target is not { Kind: FeedKind.Assets }
                ? Results.NotFound()
                : await AssetEndpoints.ExportAsync(target, path, format, recursive: true, archives, upload.Value, cancellationToken);
        });

        admin.MapPost("/assets/{directory}/folders", async (
            string directory,
            HttpContext http,
            IFeedStore feeds,
            AssetService assets,
            AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var form = await http.Request.ReadFormAsync(cancellationToken);
            var target = await feeds.FindAsync(directory, cancellationToken);
            var name = form["name"].ToString().Trim();
            if (target is { Kind: FeedKind.Assets }
                && AssetPath.TryParse(form["parent"].ToString(), out var parent)
                && !name.Contains('/', StringComparison.Ordinal)
                && AssetPath.TryParse(parent.IsRoot ? name : parent.Value + "/" + name, out var folder)
                && !folder.IsRoot
                && await assets.CreateFolderAsync(target, folder, cancellationToken) == AssetOutcome.Created)
            {
                audit.Record(http, "asset.folder.create", folder.Value, $"directory={target.Name}");
            }

            return Back(form["returnUrl"].ToString(), $"/assets/{Uri.EscapeDataString(directory)}");
        });

        admin.MapPost("/assets/{directory}/delete", async (
            string directory,
            HttpContext http,
            IFeedStore feeds,
            AssetService assets,
            AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var form = await http.Request.ReadFormAsync(cancellationToken);
            var target = await feeds.FindAsync(directory, cancellationToken);
            if (target is { Kind: FeedKind.Assets }
                && AssetPath.TryParse(form["path"].ToString(), out var path)
                && !path.IsRoot
                && await assets.DeleteAsync(target, path, recursive: true, cancellationToken) == AssetOutcome.Deleted)
            {
                audit.Record(http, "asset.delete", path.Value, $"directory={target.Name} recursive=True");
            }

            return Back(form["returnUrl"].ToString(), $"/assets/{Uri.EscapeDataString(directory)}");
        });

        admin.MapPost("/feeds/{feed}/upstreams/remove", async (
            string feed,
            HttpContext http,
            IFeedStore feeds,
            AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var form = await http.Request.ReadFormAsync(cancellationToken);
            var target = await feeds.FindAsync(feed, cancellationToken);
            if (target is not null && int.TryParse(form["key"].ToString(), out var upstreamKey))
            {
                await feeds.RemoveUpstreamAsync(target.Key, upstreamKey, cancellationToken);
                audit.Record(http, "upstream.remove", upstreamKey.ToString(CultureInfo.InvariantCulture), $"feed={feed}");
            }

            return Back(form["returnUrl"].ToString(), $"/admin/feeds/{Uri.EscapeDataString(feed)}");
        });

        admin.MapPost("/feeds/{feed}/upstreams/move", async (
            string feed,
            HttpContext http,
            IFeedStore feeds,
            AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var form = await http.Request.ReadFormAsync(cancellationToken);
            var target = await feeds.FindAsync(feed, cancellationToken);
            var direction = form["direction"].ToString();
            if (target is not null
                && int.TryParse(form["key"].ToString(), out var upstreamKey)
                && direction is "up" or "down"
                && await feeds.MoveUpstreamAsync(target.Key, upstreamKey, direction == "up", cancellationToken))
            {
                audit.Record(http, "upstream.move", upstreamKey.ToString(CultureInfo.InvariantCulture), $"feed={feed} direction={direction}");
            }

            return Back(form["returnUrl"].ToString(), $"/admin/feeds/{Uri.EscapeDataString(feed)}");
        });
    }

    /// <summary>A return address with what a pull did, as counts, replacing what an earlier pull left there.</summary>
    private static string WithPullResult(string returnUrl, PullReport report)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return returnUrl;
        }

        string[] ours = ["pulled=", "present=", "missing=", "capped="];
        var query = returnUrl.IndexOf('?', StringComparison.Ordinal);
        var path = query < 0 ? returnUrl : returnUrl[..query];
        var pairs = query < 0
            ? []
            : returnUrl[(query + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries).Where(p => !ours.Any(o => p.StartsWith(o, StringComparison.Ordinal))).ToList();
        pairs.Add("pulled=" + report.Fetched.Count.ToString(CultureInfo.InvariantCulture));
        pairs.Add("present=" + report.AlreadyHere.Count.ToString(CultureInfo.InvariantCulture));
        pairs.Add("missing=" + report.Unavailable.Count.ToString(CultureInfo.InvariantCulture));
        if (report.StoppedAtLimit)
        {
            pairs.Add("capped=1");
        }

        return path + "?" + string.Join('&', pairs);
    }

    /// <summary>A return address with the outcome of a fetch added, replacing one left by an earlier fetch.</summary>
    private static string WithResult(string returnUrl, string code)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return returnUrl;
        }

        var query = returnUrl.IndexOf('?', StringComparison.Ordinal);
        var path = query < 0 ? returnUrl : returnUrl[..query];
        var pairs = query < 0
            ? []
            : returnUrl[(query + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith("fetch=", StringComparison.Ordinal)).ToList();
        pairs.Add("fetch=" + code);
        return path + "?" + string.Join('&', pairs);
    }

    /// <summary>
    /// Paths answered for clients rather than people: the protocols, the management API, health probes and the
    /// framework's own files. Their errors are part of their contract, so no HTML error page replaces them.
    /// </summary>
    public static bool IsProtocolPath(PathString path) =>
        path.StartsWithSegments("/nuget", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/endpoints", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/themes", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase);

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
            MergePushedIdsWithUpstreams = seed.MergePushedIdsWithUpstreams,
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
