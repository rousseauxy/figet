using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using FiGet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Web.Security;

/// <summary>
/// The master key the data-protection key ring is encrypted with, from <c>FiGet:DataProtection:MasterKey</c>: 32 random
/// bytes, base64. Found by the 2026-09-14 review: the ring sat in the database as plain XML, and the same keys protect the
/// sign-in cookie - so anyone who could read the database (a backup, a reporting login, a leaked connection string) could
/// forge a super admin's cookie without any password. With a master key from a secret, the database alone is not enough.
/// </summary>
public sealed class KeyRingMasterKey
{
    public const string Setting = "FiGet:DataProtection:MasterKey";

    private KeyRingMasterKey(byte[]? key)
    {
        Key = key;
        Id = key is null ? null : Convert.ToHexStringLower(SHA256.HashData(key))[..16];
    }

    public byte[]? Key { get; }

    /// <summary>Names the key without revealing it, so a ring encrypted with another key is told apart from a damaged one.</summary>
    public string? Id { get; }

    public bool IsSet => Key is not null;

    /// <summary>The key from its setting; none when empty. Anything that is not 32 bytes of base64 refuses to start.</summary>
    public static KeyRingMasterKey From(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return new KeyRingMasterKey(null);
        }

        var buffer = new byte[64];
        if (!Convert.TryFromBase64String(base64.Trim(), buffer, out var length) || length != 32)
        {
            throw new InvalidOperationException($"{Setting} must be 32 random bytes in base64, for example from: openssl rand -base64 32");
        }

        return new KeyRingMasterKey(buffer[..32]);
    }
}

/// <summary>Encrypts each key's secret part with AES-GCM under the master key, as the framework's certificate encryptor would.</summary>
public sealed class MasterKeyXmlEncryptor(KeyRingMasterKey masterKey) : IXmlEncryptor
{
    /// <summary>Bound into every ciphertext, so an element encrypted for something else does not decrypt as a key.</summary>
    internal static readonly byte[] Purpose = Encoding.UTF8.GetBytes("FiGet data-protection key ring, v1");

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);
        var key = masterKey.Key ?? throw new InvalidOperationException($"{KeyRingMasterKey.Setting} is not set.");
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var ciphertext = new byte[plaintext.Length];
        using (var aes = new AesGcm(key, tag.Length))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Purpose);
        }

        CryptographicOperations.ZeroMemory(plaintext);
        var element = new XElement(
            "encryptedKey",
            new XAttribute("masterKeyId", masterKey.Id!),
            new XElement("nonce", Convert.ToBase64String(nonce)),
            new XElement("tag", Convert.ToBase64String(tag)),
            new XElement("value", Convert.ToBase64String(ciphertext)));
        return new EncryptedXmlInfo(element, typeof(MasterKeyXmlDecryptor));
    }
}

/// <summary>Created by the framework through its activator, which hands it the service provider.</summary>
public sealed class MasterKeyXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);
        var masterKey = services.GetService(typeof(KeyRingMasterKey)) as KeyRingMasterKey;
        if (masterKey?.Key is not { } key)
        {
            throw new InvalidOperationException($"The data-protection key ring is encrypted with a master key, and {KeyRingMasterKey.Setting} is not set.");
        }

        if ((string?)encryptedElement.Attribute("masterKeyId") != masterKey.Id)
        {
            throw new InvalidOperationException($"The data-protection key ring was encrypted with a different master key than {KeyRingMasterKey.Setting} holds.");
        }

        var nonce = Convert.FromBase64String((string)encryptedElement.Element("nonce")!);
        var tag = Convert.FromBase64String((string)encryptedElement.Element("tag")!);
        var ciphertext = Convert.FromBase64String((string)encryptedElement.Element("value")!);
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(key, tag.Length))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, MasterKeyXmlEncryptor.Purpose);
        }

        try
        {
            return XElement.Parse(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}

public static class KeyRingEncryption
{
    private static readonly XNamespace DataProtection = "http://schemas.asp.net/2015/03/dataProtection";

    /// <summary>
    /// Encrypts the keys stored before a master key was configured, in place, the way the framework encrypts a new one: each
    /// element marked <c>requiresEncryption</c> becomes an <c>encryptedSecret</c> naming its decryptor. The keys themselves do
    /// not change, so sign-in cookies and stored provider secrets stay readable. Returns how many keys were encrypted.
    /// </summary>
    public static async Task<int> EncryptStoredKeysAsync(FiGetDbContext db, IXmlEncryptor encryptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(encryptor);
        var encrypted = 0;
        foreach (var row in await db.DataProtectionKeys.ToListAsync(cancellationToken))
        {
            if (string.IsNullOrEmpty(row.Xml))
            {
                continue;
            }

            var key = XElement.Parse(row.Xml);
            var secrets = key.Descendants()
                .Where(e => string.Equals((string?)e.Attribute(DataProtection + "requiresEncryption"), "true", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (secrets.Count == 0)
            {
                continue;
            }

            foreach (var secret in secrets)
            {
                var info = encryptor.Encrypt(new XElement(secret));
                secret.ReplaceWith(new XElement(
                    DataProtection + "encryptedSecret",
                    new XAttribute("decryptorType", info.DecryptorType.AssemblyQualifiedName!),
                    info.EncryptedElement));
            }

            row.Xml = key.ToString(SaveOptions.DisableFormatting);
            encrypted++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return encrypted;
    }
}
