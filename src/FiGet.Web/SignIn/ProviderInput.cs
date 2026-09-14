using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;

namespace FiGet.Web.SignIn;

/// <summary>The provider form on the admin pages, for adding one and for editing one.</summary>
public sealed partial class ProviderInput
{
    // Every string is kept non-null: a form posts an empty input as nothing, and the binder turns that into null.

    [Required(ErrorMessage = "Enter a name for the button.")]
    [StringLength(64)]
    public string DisplayName { get; set => field = value ?? ""; } = "";

    [Required(ErrorMessage = "Enter a slug.")]
    [RegularExpression("^[a-z0-9][a-z0-9-]{0,31}$", ErrorMessage = "Lower-case letters, digits and dashes, at most 32.")]
    public string Slug { get; set => field = value ?? ""; } = "";

    [Required(ErrorMessage = "Enter the issuer URL.")]
    [StringLength(512)]
    public string Authority { get; set => field = value ?? ""; } = "";

    [Required(ErrorMessage = "Enter the client id.")]
    [StringLength(256)]
    public string ClientId { get; set => field = value ?? ""; } = "";

    /// <summary>Empty when editing keeps the stored secret.</summary>
    [StringLength(1024)]
    public string ClientSecret { get; set => field = value ?? ""; } = "";

    [StringLength(512)]
    public string Scopes { get; set => field = value ?? ""; } = OidcProvider.DefaultScopes;

    [Required(ErrorMessage = "Enter the claim to take user names from.")]
    [StringLength(64)]
    public string UserNameClaim { get; set => field = value ?? ""; } = OidcProvider.DefaultUserNameClaim;

    [StringLength(64)]
    public string GroupsClaim { get; set => field = value ?? ""; } = "";

    /// <summary>False by default: an unticked checkbox sends nothing, so a default of true would re-enable on every save.</summary>
    public bool Enabled { get; set; }

    public int Ordinal { get; set; }

    /// <summary>False by default for the same reason as <see cref="Enabled"/>; a new provider starts not making accounts.</summary>
    public bool CreateAccounts { get; set; }

    [StringLength(1024)]
    public string AllowedEmailDomains { get; set => field = value ?? ""; } = "";

    public static ProviderInput From(OidcProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new ProviderInput
        {
            DisplayName = provider.DisplayName,
            Slug = provider.Slug,
            Authority = provider.Authority,
            ClientId = provider.ClientId,
            Scopes = provider.Scopes,
            UserNameClaim = provider.UserNameClaim,
            CreateAccounts = provider.CreateAccounts,
            AllowedEmailDomains = provider.AllowedEmailDomains,
            GroupsClaim = provider.GroupsClaim,
            Enabled = provider.Enabled,
            Ordinal = provider.Ordinal,
        };
    }

    /// <summary>A message for what the attributes cannot check, or null when the input is usable.</summary>
    public string? Problem()
    {
        // Plain http only on this machine, which is the rule the sign-in handler applies to the provider's metadata: refused
        // here, rather than saved and found broken at the first sign-in.
        if (!Uri.TryCreate(Authority.Trim(), UriKind.Absolute, out var authority)
            || !(authority.Scheme == "https" || (authority.Scheme == "http" && authority.IsLoopback)))
        {
            return "The issuer must be an absolute https:// URL.";
        }

        if (OidcProvider.ParseEmailDomains(AllowedEmailDomains).Any(d => !DomainPattern().IsMatch(d)))
        {
            return "Allowed email domains are host names such as example.org, one per line.";
        }

        return ClaimPattern().IsMatch(UserNameClaim.Trim()) && (GroupsClaim.Trim().Length == 0 || ClaimPattern().IsMatch(GroupsClaim.Trim()))
            ? null
            : "Claim names are letters, digits and . _ : / -.";
    }

    /// <summary>Copies the form onto a provider. The secret is replaced only when one was typed.</summary>
    public void ApplyTo(OidcProvider provider, ISecretProtector secrets, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(secrets);
        provider.DisplayName = DisplayName.Trim();
        provider.Slug = Slug.Trim().ToLowerInvariant();
        provider.Authority = Authority.Trim().TrimEnd('/');
        provider.ClientId = ClientId.Trim();
        if (ClientSecret.Length > 0)
        {
            provider.ProtectedClientSecret = secrets.Protect(ClientSecret.Trim());
        }

        provider.Scopes = string.Join(' ', Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        provider.UserNameClaim = UserNameClaim.Trim();
        provider.GroupsClaim = GroupsClaim.Trim();
        provider.Enabled = Enabled;
        provider.CreateAccounts = CreateAccounts;
        provider.AllowedEmailDomains = string.Join('\n', OidcProvider.ParseEmailDomains(AllowedEmailDomains));
        provider.Ordinal = Ordinal;
        provider.UpdatedUtc = utcNow;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._:/-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ClaimPattern();

    [GeneratedRegex(@"^(?=.{1,253}$)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();
}
