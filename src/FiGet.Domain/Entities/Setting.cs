namespace FiGet.Domain.Entities;

/// <summary>
/// One piece of server configuration an administrator can change while it runs, stored rather than
/// configured. Configuration stays the fallback: a fresh instance has no rows and answers from
/// appsettings, which is what makes a container with no database yet still start with a look.
/// </summary>
public sealed class Setting
{
    /// <summary>The lower-cased key, and the primary key: lookups never depend on a collation.</summary>
    public required string Key { get; set; }

    public string Value { get; set; } = "";

    public DateTime UpdatedUtc { get; set; }

    /// <summary>Who last wrote it, so a surprising setting can be asked about rather than guessed at.</summary>
    public string? UpdatedBy { get; set; }
}

/// <summary>The keys this application stores, named once so a typo cannot invent a second setting.</summary>
public static class SettingKeys
{
    /// <summary>Name of the active theme pack. Empty means "whatever configuration says".</summary>
    public const string Theme = "theming:theme";
}
