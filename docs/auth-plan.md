# Accounts, single sign-on and permissions

Replaces the admin-token sign-in with real accounts: local users with passwords, any number of OpenID Connect
providers side by side, roles, groups, per-feed permissions, and API keys that belong to a user. Decided with the
owner on 2026-09-13; the questions and answers are recorded under "Decisions". Build section 8 of
`docs/build-plan.md` sketched this; where the two differ, this document wins.

## Decisions

| Question | Decision |
|---|---|
| Roles | **Super admin, admin, user.** A super admin can do everything, including managing admins, sign-in providers and instance settings. An admin manages feeds, upstreams, users, groups, permissions and service tokens. A user signs in, manages their own profile and keys, and gets feed access through permissions. |
| What a user may do with feeds | **Per-feed permissions, granted to users and to groups.** |
| Permission levels | **Read, Publish, Manage**, each including the ones before it. Read: list and download. Publish: push, upload, unlist, relist, delete versions or files. Manage: that feed's settings, upstreams and access list. Admins and super admins hold Manage on every feed. Asset directories use the same levels. |
| Anonymous read | Unchanged: a per-feed switch. It grants Read to everyone, signed in or not. |
| First administrator | `admin` / `admin`, role super admin, created when no user exists. The first sign-in must set a new password before anything else works. |
| OIDC providers | **Configured by a super admin in the admin UI.** Any number, enabled side by side (Google, Entra ID, Authentik, Keycloak...). The client secret is stored encrypted with the instance's data-protection keys, never shown again after saving. Changes apply without a restart. |
| First OIDC sign-in | **Never matched automatically.** A provider identity with no link creates a new user with role *user*. Joining it to an existing account is done from that account's profile page. |
| Provider groups | **Optional mapping per provider.** A provider names its groups claim; a FiGet group can be linked to a group of a provider. Membership from a linked group is refreshed at every sign-in with that provider. Provider groups never grant a role. |
| Sign-in page with SSO enabled | **A super admin setting**: either provider buttons with a "sign in with a local account" link, or provider buttons only. `/account/login/local` always works either way. |
| Personal API keys | **Act as their owner, optionally narrower**: never more than the user's current permissions (which follow group and role changes immediately), can be limited to some feeds and to read-only, and stop working when the user is disabled or deleted. |

## Added to the owner's outline

Not asked, because each has one sensible answer; listed so they are visible.

- **Passwords** are hashed with ASP.NET Core's `PasswordHasher` (PBKDF2, versioned, rehashed on sign-in when the
  algorithm moves on). Minimum length 12 for a password a person chooses. No composition rules.
- **Lockout**: five failed sign-ins lock a local account for fifteen minutes. Failures are recorded with the caller's
  address. The response does not say whether the user exists.
- **Sessions end when access changes.** Each user has a security stamp, changed on password change, role change,
  disable, and deletion; the sign-in cookie is checked against it, so a demoted or disabled user is signed out on
  their next request rather than when the cookie expires.
- **The last super admin** cannot be demoted, disabled or deleted.
- **Recovery** when nobody can sign in: `FiGet:Auth:Recovery:UserName` and `FiGet:Auth:Recovery:Password`, set as
  environment variables, reset that user on start - enabled, unlocked, super admin, must change password - and write
  an audit entry. Remove them afterwards; the log warns on every start while they are set.
- **Service tokens** are what exists today: admin-managed keys not tied to a person, limited to a feed and to
  scopes. Only a super admin may create one that carries instance-wide admin rights. Nobody can create a key with more
  rights than they hold themselves - the ceiling is enforced in the token service, whichever page asks.
- **Protocol clients** keep sending keys as they do now (`X-NuGet-ApiKey`, `X-ApiKey`, Basic password, Bearer),
  and both key kinds are accepted there. A user's password is never accepted by a protocol endpoint.
- **The admin-token sign-in is removed.** `FiGet:Auth:BootstrapAdminToken` stays as a way to create a service token
  for automation on first start, but no longer signs anyone in to the web UI.
- **Audit log**: a table and an admin page, filterable by user, feed, event and date, pruned after a configurable
  number of days. Records sign-ins (local and provider, success and failure), lockouts, password changes and resets,
  user, group, role and permission changes, provider changes, key creation and revocation, refused keys, and the
  admin and package events already written to the console today.
- **Profile page**: display name, email, password change (local accounts), linked providers with connect and
  disconnect, personal API keys. Disconnecting the last way to sign in is refused.
- **Deleting a user** removes their links, memberships, permissions and personal keys; audit entries keep the name.

## Data model

| Table | Holds |
|---|---|
| `Users` | user name (unique), display name, email, password hash (null for provider-only accounts), role, disabled, must-change-password, security stamp, failed-attempt count, locked-until, created, last sign-in |
| `ExternalLogins` | user, provider, provider subject (unique per provider), email as the provider gave it, linked at |
| `OidcProviders` | slug (used in the callback path), display name, authority, client id, protected secret, scopes, user-name claim, groups claim, enabled, order |
| `Groups` | name (unique), description |
| `GroupMembers` | group, user, source: manual or provider (a provider refresh replaces only its own rows) |
| `GroupProviderLinks` | group, provider, provider group name |
| `FeedPermissions` | feed, user or group, level |
| `AccessTokens` | as today, plus an owning user for personal keys (null for service tokens) |
| `AuditEntries` | when, actor, event, target, feed, details, caller address |

## Effective permission

For a request on a feed:

1. Admin or super admin: Manage.
2. Otherwise the highest of: Read if the feed allows anonymous read; the user's own grant; every grant of every group
   the user is in.
3. A personal key: the lower of step 2 for its owner and the key's own limits. A service token: its scopes, as today.

Evaluated per request, so a change applies at once. Browsing pages use the same evaluation as the protocols.

## OpenID Connect mechanics

- One `OpenIdConnect` authentication scheme per enabled provider, named `oidc-{slug}`, with callback
  `/signin-oidc/{slug}`. Schemes are added and removed at runtime through `IAuthenticationSchemeProvider`, and the
  options cache for a changed provider is cleared, so saving a provider needs no restart.
- The public base URL (`FiGet:PublicBaseUrl` or forwarded headers) must be right for callbacks behind a proxy; the
  provider page shows the exact redirect URI to register.
- Sign-in: provider identity found in `ExternalLogins` → that user (refused if disabled). Not found → a new user with
  role *user*, user name from the configured claim, made unique. Linked groups refreshed from the groups claim.
- Connect from the profile page: a challenge marked as a link request for the signed-in user; refused when that
  identity already belongs to another user.
- Signing out ends the FiGet session; ending the provider's session is not attempted.

## Phases

Each phase ships on its own: migrations for both providers, tests on both databases, deployed and checked live.

1. **Local accounts and roles.** Users table, password sign-in, first-start `admin`/`admin` with forced change,
   lockout, security stamp, recovery variables, users admin page (admins manage users; super admins manage admins),
   profile page with password change, token sign-in removed, audit entries to the console as today.
2. **Groups and per-feed permissions.** Groups page, an Access section on each feed's settings, effective-permission
   evaluation in protocol requests and browsing pages, tests for every level.
3. **API keys.** Personal keys on the profile page, service tokens restricted to admins, the ceiling rule.
4. **OpenID Connect.** Providers page, runtime schemes, sign-in page modes and the local fallback, account creation,
   connect and disconnect on the profile page, group mapping. Verified against a real Authentik provider.
5. **Audit log.** Table, admin page with filters, retention and pruning; the console lines stay.
