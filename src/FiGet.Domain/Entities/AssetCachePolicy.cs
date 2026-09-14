namespace FiGet.Domain.Entities;

/// <summary>How downloads under a folder tell caches to behave. A folder without a row inherits from the folder above it.</summary>
public enum AssetCacheMode
{
    /// <summary>
    /// <c>Cache-Control: no-store, no-cache, must-revalidate</c>, <c>Pragma: no-cache</c>, <c>Expires: -1</c>: what a web
    /// server's virtual folder sent for files that must always be fetched afresh.
    /// </summary>
    NoStore,

    /// <summary><c>Cache-Control: public, max-age=N</c>.</summary>
    MaxAge,
}

/// <summary>
/// The cache mode of one folder of an asset directory, inherited by every folder below it until another row says
/// otherwise. Kept in FiGet rather than beside the files, because on a folder-backed directory nothing can be written
/// beside them. Fixed modes, not free-form headers, so a mode can never switch off the sandbox policy or the download
/// disposition every asset response carries.
/// </summary>
public sealed class AssetCachePolicy
{
    public int Key { get; set; }

    public int FeedKey { get; set; }

    /// <summary>The folder, lower-cased; empty for the directory itself.</summary>
    public required string PathLower { get; set; }

    public AssetCacheMode Mode { get; set; }

    /// <summary>Seconds, for <see cref="AssetCacheMode.MaxAge"/>.</summary>
    public int? MaxAgeSeconds { get; set; }
}
