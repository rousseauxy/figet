using System.Net;
using FiGet.Application.Ports;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FiGet.Integration.Tests;

/// <summary>
/// The usual server, with a settings store that always suspends before answering, as SQL Server does and SQLite in a test
/// run usually does not. A page that awaits a setting is then rendered once while the await is pending, with whatever
/// state it had set before it.
/// </summary>
public sealed class SuspendingStoreFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISettingStore>();
            services.AddScoped<FiGet.Infrastructure.Persistence.Stores.EfSettingStore>();
            services.AddScoped<ISettingStore>(provider =>
                new SuspendingSettingStore(provider.GetRequiredService<FiGet.Infrastructure.Persistence.Stores.EfSettingStore>()));
        });
    }

    private sealed class SuspendingSettingStore(ISettingStore inner) : ISettingStore
    {
        public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return await inner.GetAsync(key, cancellationToken);
        }

        public async Task SetAsync(string key, string value, string? updatedBy, CancellationToken cancellationToken)
        {
            await Task.Yield();
            await inner.SetAsync(key, value, updatedBy, cancellationToken);
        }
    }
}

public sealed class SuspendingStoreTests(SuspendingStoreFixture server) : IClassFixture<SuspendingStoreFixture>
{
    /// <summary>
    /// Found on a SQL Server instance: the Appearance page answered 500, "EditForm requires either a Model parameter", every
    /// time, while the same build on SQLite showed it. Its form model was set only after the settings read, and the render
    /// Blazor makes while that read is pending reached the form with no model.
    /// </summary>
    [Fact]
    public async Task Admin_pages_render_when_reading_a_setting_suspends()
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() })
        {
            BaseAddress = server.BaseAddress,
        };
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));

        foreach (var path in new[] { "/admin/appearance", "/admin/providers", "/admin/changes", "/admin/system", "/account/profile" })
        {
            await HttpAssert.SuccessBodyAsync(await client.GetAsync(path));
        }
    }
}
