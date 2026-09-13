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

    /// <summary>What the sign-in page offers once a provider is enabled; see <see cref="SignInModes"/>.</summary>
    public const string SignInMode = "auth:signin-mode";
}

/// <summary>The values of <see cref="SettingKeys.SignInMode"/>. <c>/account/login/local</c> shows the local form in both.</summary>
public static class SignInModes
{
    /// <summary>Provider buttons, and a link to sign in with a local account. The default.</summary>
    public const string ProvidersAndLocal = "providers-and-local";

    /// <summary>Provider buttons only.</summary>
    public const string ProvidersOnly = "providers-only";
}
