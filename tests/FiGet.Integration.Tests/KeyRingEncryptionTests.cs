using System.Net;
using System.Security.Cryptography;
using FiGet.Infrastructure.Persistence;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Web.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>
/// The data-protection key ring encrypted with a master key. Found by the 2026-09-14 review: stored as plain XML, the ring
/// let anyone who could read the database forge a sign-in cookie.
/// </summary>
public sealed class KeyRingEncryptionTests : IDisposable
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), "figet-keyring-" + Guid.NewGuid().ToString("N") + ".db");

    /// <summary>
    /// A ring written before a master key existed is encrypted in place, and what it protected stays readable - with the
    /// master key, and only with it.
    /// </summary>
    [Fact]
    public async Task A_plain_key_ring_is_encrypted_in_place_and_still_opens_what_it_protected()
    {
        var masterKey = KeyRingMasterKey.From(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        string protectedText;
        using (var legacy = Provider(masterKey: null))
        {
            await using (var scope = legacy.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<FiGetDbContext>().Database.EnsureCreatedAsync();
            }

            protectedText = legacy.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("provider secret");
        }

        Assert.Contains("requiresEncryption", await StoredXmlAsync(), StringComparison.Ordinal);

        await using (var scope = Provider(masterKey: null).CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FiGetDbContext>();
            Assert.Equal(1, await KeyRingEncryption.EncryptStoredKeysAsync(db, new MasterKeyXmlEncryptor(masterKey), CancellationToken.None));

            // Every start runs it; the second time there is nothing left to encrypt.
            Assert.Equal(0, await KeyRingEncryption.EncryptStoredKeysAsync(db, new MasterKeyXmlEncryptor(masterKey), CancellationToken.None));
        }

        var stored = await StoredXmlAsync();
        Assert.DoesNotContain("requiresEncryption", stored, StringComparison.Ordinal);
        Assert.Contains("encryptedSecret", stored, StringComparison.Ordinal);

        using (var withKey = Provider(masterKey))
        {
            Assert.Equal("provider secret", withKey.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedText));

            // A key made from here on is written encrypted too.
            withKey.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        }

        Assert.DoesNotContain("requiresEncryption", await StoredXmlAsync(), StringComparison.Ordinal);

        using (var withoutKey = Provider(masterKey: null))
        {
            Assert.ThrowsAny<CryptographicException>(() => withoutKey.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedText));
        }

        using (var otherKey = Provider(KeyRingMasterKey.From(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
        {
            Assert.ThrowsAny<CryptographicException>(() => otherKey.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedText));
        }
    }

    [Theory]
    [InlineData("not base64 at all")]
    [InlineData("c2hvcnQ=")]
    public void A_master_key_that_is_not_32_bytes_of_base64_is_refused(string value) =>
        Assert.Throws<InvalidOperationException>(() => KeyRingMasterKey.From(value));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(databasePath);
    }

    /// <summary>The same data-protection set-up the server has, over a database of this test's own.</summary>
    private ServiceProvider Provider(KeyRingMasterKey? masterKey)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<FiGetDbContext>(o => o.UseSqlite($"Data Source={databasePath}"));
        services.AddDataProtection().SetApplicationName("FiGet").PersistKeysToDbContext<FiGetDbContext>();
        services.AddSingleton(masterKey ?? KeyRingMasterKey.From(null));
        if (masterKey is not null)
        {
            services.Configure<KeyManagementOptions>(o => o.XmlEncryptor = new MasterKeyXmlEncryptor(masterKey));
        }

        return services.BuildServiceProvider();
    }

    private async Task<string> StoredXmlAsync()
    {
        await using var scope = Provider(masterKey: null).CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<FiGetDbContext>().DataProtectionKeys.Select(k => k.Xml).ToListAsync();
        return string.Join("\n", rows);
    }
}

public sealed class MasterKeyServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting(KeyRingMasterKey.Setting, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }
}

public sealed class MasterKeyServerTests(MasterKeyServerFixture server) : IClassFixture<MasterKeyServerFixture>
{
    /// <summary>A server with a master key signs people in as before, and nothing it stored is readable without the key.</summary>
    [Fact]
    public async Task A_server_with_a_master_key_signs_in_and_stores_its_keys_encrypted()
    {
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(browser));
        await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/admin/feeds"));

        await using var scope = server.Services.CreateAsyncScope();
        var keys = await scope.ServiceProvider.GetRequiredService<FiGetDbContext>().DataProtectionKeys.Select(k => k.Xml).ToListAsync();
        Assert.NotEmpty(keys);
        Assert.All(keys, xml => Assert.DoesNotContain("requiresEncryption", xml!, StringComparison.Ordinal));
    }
}
