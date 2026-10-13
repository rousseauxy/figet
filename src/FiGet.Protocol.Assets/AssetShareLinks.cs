using System.Security.Cryptography;
using System.Text.Json;
using FiGet.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace FiGet.Protocol.Assets;

/// <summary>How long a download link may live. Set from <c>FiGet:Assets:ShareLinks</c>.</summary>
public sealed class ShareLinkOptions
{
    /// <summary>The longest a link may be asked for, in hours. 0 switches links off: none is made and none is honoured.</summary>
    public int MaxHours { get; set; } = 168;

    /// <summary>What a request that names no lifetime gets, in hours; never more than <see cref="MaxHours"/>.</summary>
    public int DefaultHours { get; set; } = 24;
}

/// <summary>What a download link points at, as it was when the link was made.</summary>
public sealed record ShareLinkTarget(int FeedKey, string Directory, string Path, string Content);

/// <summary>
/// Download links for one file that work without credentials until they expire: handing a file from a directory that
/// asks for a key to a machine or a person that has none, without opening the directory.
///
/// A link is the server's own sealed statement of "this file, this content, until then", made with the key ring that
/// protects the sign-in cookies. Nothing is stored, so every replica honours a link any of them made, and there is no
/// table of links to clean. The price is that one link cannot be withdrawn on its own: it ends when it expires, when its
/// file is replaced or deleted, or when the directory is. That is why a link names the content and not only the path - a
/// file replaced after the link was handed out is not what the link was given for.
/// </summary>
public sealed class AssetShareLinks(IDataProtectionProvider protection, IOptions<ShareLinkOptions> options, TimeProvider time)
{
    private readonly ITimeLimitedDataProtector protector = protection.CreateProtector("FiGet.Assets.ShareLink.v1").ToTimeLimitedDataProtector();

    public bool Enabled => MaxHours > 0;

    public int MaxHours => Math.Max(0, options.Value.MaxHours);

    public int DefaultHours => Math.Clamp(options.Value.DefaultHours, 1, Math.Max(1, MaxHours));

    /// <summary>
    /// What identifies a file's content: its hash when FiGet stored the file, size and modified time when the file is a
    /// folder's and was never hashed. The same value a download sends as its entity tag.
    /// </summary>
    public static string ContentOf(AssetItem file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.Sha256 ?? $"{file.Size:x}-{file.ModifiedUtc.Ticks:x}";
    }

    /// <summary>A link token for the file, valid for <paramref name="hours"/> from now.</summary>
    public (string Token, DateTimeOffset ExpiresUtc) Create(Feed directory, AssetItem file, int hours) =>
        CreateUntil(directory, file, time.GetUtcNow().AddHours(hours));

    /// <summary>A link token that ends at a given moment. Apart from <see cref="Create"/>, for tests of an expired link.</summary>
    public (string Token, DateTimeOffset ExpiresUtc) CreateUntil(Feed directory, AssetItem file, DateTimeOffset expiresUtc)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(file);
        var target = new ShareLinkTarget(directory.Key, directory.NameLower, file.Path, ContentOf(file));
        return (protector.Protect(JsonSerializer.Serialize(target), expiresUtc), expiresUtc);
    }

    /// <summary>What a token points at, or null when it is not one of this server's, was altered, or has expired.</summary>
    public ShareLinkTarget? Read(string? token) => Read(token, out _);

    /// <summary>The same, with when the link ends: what a page shows beside a link it just made.</summary>
    public ShareLinkTarget? Read(string? token, out DateTimeOffset expiresUtc)
    {
        expiresUtc = default;
        if (string.IsNullOrWhiteSpace(token) || !Enabled)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ShareLinkTarget>(protector.Unprotect(token, out expiresUtc));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }
}
