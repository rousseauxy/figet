using System.Security.Claims;
using FiGet.Application.Accounts;
using FiGet.Domain.Entities;

namespace FiGet.Http;

/// <summary>
/// The claims a sign-in cookie carries, and the account they describe. Here rather than in the web host because
/// protocol requests read them too: a signed-in browser downloads from a feed it may read.
/// </summary>
public static class AccountClaims
{
    /// <summary>The signed-in account's key.</summary>
    public const string UserKey = "figet:user";

    /// <summary>The account's security stamp when the cookie was issued; a different stamp now ends the session.</summary>
    public const string Stamp = "figet:stamp";

    /// <summary>Role claim values. A super admin carries "admin" too, so every admin check also admits them.</summary>
    public const string AdminRole = "admin";
    public const string SuperAdminRole = "superadmin";
    public const string UserRole = "user";

    /// <summary>The signed-in account as the access rules see it, or null when nobody is signed in.</summary>
    public static AccountActor? Actor(ClaimsPrincipal? user)
    {
        if (user is null || !int.TryParse(user.FindFirst(UserKey)?.Value, out var key))
        {
            return null;
        }

        var role = user.IsInRole(SuperAdminRole) ? Domain.Entities.UserRole.SuperAdmin
            : user.IsInRole(AdminRole) ? Domain.Entities.UserRole.Admin
            : Domain.Entities.UserRole.User;
        return new AccountActor(key, role);
    }
}
