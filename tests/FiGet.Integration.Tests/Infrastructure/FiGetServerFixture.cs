using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FiGet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace FiGet.Integration.Tests.Infrastructure;

public enum TestDatabase
{
    Sqlite,
    SqlServer,
}

/// <summary>
/// Runs FiGet on real Kestrel (so NuGet's own client library talks to it over a socket) with a fresh
/// database and storage directory. Feeds: <c>public</c> (anonymous read, unlist on delete),
/// <c>private</c> (token read), <c>overwrite</c> (overwrite allowed, hard delete).
/// </summary>
public abstract class FiGetServerFixture : IAsyncLifetime
{
    /// <summary>Set to a SQL Server connection string without a database name to run the SQL Server tests.</summary>
    public const string SqlServerVariable = "FIGET_TEST_SQLSERVER";

    public const string AdminToken = "figet_test_admin_token_0123456789abcdef";

    private readonly string root = Path.Combine(Path.GetTempPath(), "figet-it-" + Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program>? factory;
    private string? sqlServerDatabase;

    protected FiGetServerFixture(TestDatabase database) => Database = database;

    public TestDatabase Database { get; }

    /// <summary>Null when the provider is not available in this environment (SQL Server without the variable).</summary>
    public string? SkipReason { get; private set; }

    public Uri BaseAddress { get; private set; } = null!;

    public IServiceProvider Services => factory!.Services;

    public async ValueTask InitializeAsync()
    {
        string provider;
        string connectionString;
        if (Database == TestDatabase.SqlServer)
        {
            var server = Environment.GetEnvironmentVariable(SqlServerVariable);
            if (string.IsNullOrWhiteSpace(server))
            {
                SkipReason = $"Set {SqlServerVariable} to run the SQL Server integration tests.";
                return;
            }

            sqlServerDatabase = "figet_it_" + Guid.NewGuid().ToString("N")[..12];
            provider = "SqlServer";
            connectionString = server.TrimEnd(';') + ";Database=" + sqlServerDatabase;
        }
        else
        {
            provider = "Sqlite";
            connectionString = $"Data Source={Path.Combine(root, "figet.db")}";
        }

        Directory.CreateDirectory(root);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            var settings = new Dictionary<string, string>
            {
                ["FiGet:Storage:Root"] = root,
                ["FiGet:Database:Provider"] = provider,
                ["FiGet:Database:ConnectionString"] = connectionString,
                ["FiGet:Auth:BootstrapAdminToken"] = AdminToken,
                ["FiGet:Feeds:0:Name"] = "public",
                ["FiGet:Feeds:0:AnonymousRead"] = "true",
                ["FiGet:Feeds:1:Name"] = "private",
                ["FiGet:Feeds:2:Name"] = "overwrite",
                ["FiGet:Feeds:2:AnonymousRead"] = "true",
                ["FiGet:Feeds:2:AllowOverwrite"] = "true",
                ["FiGet:Feeds:2:DeletionBehavior"] = "HardDelete",
                ["Logging:LogLevel:Default"] = "Warning",
            };
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            Configure(builder);
        });
        factory.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        factory.StartServer();

        var addresses = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        BaseAddress = new Uri(addresses.First().Replace("[::]", "127.0.0.1", StringComparison.Ordinal).Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));

        // Guard against the settings silently not reaching the app, which would run every "provider" on the
        // default SQLite file and still pass.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FiGetDbContext>();
        var expected = Database == TestDatabase.SqlServer ? "Microsoft.EntityFrameworkCore.SqlServer" : "Microsoft.EntityFrameworkCore.Sqlite";
        if (db.Database.ProviderName != expected || (Database == TestDatabase.Sqlite && !File.Exists(Path.Combine(root, "figet.db"))))
        {
            throw new InvalidOperationException($"The test server is not using the expected database: provider {db.Database.ProviderName}, expected {expected}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (factory is not null)
        {
            if (sqlServerDatabase is not null)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<FiGetDbContext>().Database.EnsureDeletedAsync();
            }

            await factory.DisposeAsync();
        }

        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
            // SQLite may still hold the file for a moment; the temp directory is disposable.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Lets a derived fixture add settings or replace services, for example a proxy feed whose upstream is
    /// a stub rather than a real gallery.
    /// </summary>
    protected virtual void Configure(IWebHostBuilder builder)
    {
    }

    /// <summary>A plain HTTP client for raw protocol assertions. Pass a token to send it as Basic credentials.</summary>
    public HttpClient CreateClient(string? basicPassword = null)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = BaseAddress };
        if (basicPassword is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("user:" + basicPassword)));
        }

        return client;
    }

    /// <summary>A NuGet client repository for a feed's v3 source, optionally with Basic credentials.</summary>
    public SourceRepository Repository(string feed, string? password = null)
    {
        var url = new Uri(BaseAddress, $"nuget/{feed}/v3/index.json").ToString();
        var source = new PackageSource(url, "figet-" + feed + "-" + Guid.NewGuid().ToString("N")[..6]) { AllowInsecureConnections = true };
        if (password is not null)
        {
            source.Credentials = new PackageSourceCredential(source.Name, "user", password, isPasswordClearText: true, validAuthenticationTypesText: null);
        }

        return NuGet.Protocol.Core.Types.Repository.Factory.GetCoreV3(source);
    }

    public static string UniqueId(string prefix) => $"{prefix}.{Guid.NewGuid().ToString("N")[..8]}";

    public void SkipIfUnavailable() => Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");
}

public sealed class SqliteServerFixture() : FiGetServerFixture(TestDatabase.Sqlite);

public sealed class SqlServerServerFixture() : FiGetServerFixture(TestDatabase.SqlServer);

public static class HttpAssert
{
    public static async Task<string> SuccessBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        return body;
    }

    public static void Status(HttpStatusCode expected, HttpResponseMessage response) =>
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode} {response.ReasonPhrase}");
}
