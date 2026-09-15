# Architecture and security review, 2026-09-14

Scope: the whole repository at commit `09013cf`, read file by file, with the design documents
(`docs/build-plan.md`, `docs/auth-plan.md`, `docs/configuration.md`, `docs/status.md`, `docs/backlog.md`) as
the statement of intent. The deployment assessed is the demanding one: two or more replicas on Kubernetes behind
a reverse proxy, SQL Server, shared or S3 storage. Nothing in production code was changed.

**This review is closed.** Everything in section 5 was acted on or answered the same day, in section 6 below; what it
left for a cluster deployment is in `docs/backlog.md`. It is kept as a record of what was looked at and decided, so a
later reader can see which risks were weighed and which were accepted.

**Evidence.** Every item is marked **Confirmed** or **Plausible**. Confirmed means a test written for this
review demonstrated it locally; those tests are in `tests/FiGet.Integration.Tests/ReviewProbeTests.cs` and
`tests/FiGet.Unit.Tests/ReviewProbeTests.cs`, uncommitted, and each asserts the *recommended* behaviour, so
each one fails (one crashes the runner) until its finding is fixed. Run one with
`FiGet.Integration.Tests.exe -method "<full name>"`; they ran against SQLite only. Plausible means the
conclusion comes from reading the code and says what would confirm it. Anything not checked is listed as
not verified in section 4, never as fine.

Line numbers are from the working tree at the commit above.

---

## 1. The ten most important items

| # | Severity | Status | Item |
|---|---|---|---|
| 1 | High | Confirmed | A feed manager can read any environment variable of the process: `CredentialRef` names it and its value is sent as a Basic password to the URL the same form sets (S5.3). |
| 2 | High | Confirmed | One anonymous request with about 1,400 nested parentheses in `$filter` overflows the stack and kills the process; the 8 KB request line allows 4,000 (S6.2). |
| 3 | High | Confirmed | Asset files are served inline with the uploader's content type and no protective headers, so an HTML or SVG upload runs as script on FiGet's origin when an admin opens its link (S6.7). |
| 4 | Medium | Plausible | Data Protection keys sit unencrypted in the database, so read access to the database forges a super-admin sign-in cookie, not only decrypts provider secrets (S8.1). |
| 5 | Medium | Confirmed | The failed-sign-in counter loses concurrent increments: thirty overlapping wrong passwords left it at one and the account unlocked (S1.2). |
| 6 | Medium | Confirmed | The audit log's caller address is the raw `X-Forwarded-For` header, which the client writes, and it keys the audit throttles (S7.1). |
| 7 | Medium | Plausible | An OpenID Connect provider has no email or domain allow-list, so with a public provider anyone gets an account, and a signed-in account is exempt from the anonymous rate limit (S2.3). |
| 8 | Medium | Plausible | Upstream URLs typed by feed managers bypass the private-network guard that "fetch by URL" has, and plain HTTP is allowed (S5.2). |
| 9 | Medium | Confirmed | Every id a client asks a proxy feed about becomes a catalogue row and an upstream round-trip, whether or not the id exists (S7.4). |
| 10 | Medium | Confirmed | The documented forwarded-headers switch does not honour `X-Forwarded-Host`, and it trusts every peer as a proxy (S10.3). |

---

## 2. Security findings by area

### 2.1 Accounts and sessions

**Holds.** Passwords use ASP.NET Core Identity's PBKDF2 hasher with per-version rehash on sign-in
(`src/FiGet.Infrastructure/Accounts/PlatformPasswordHasher.cs`). The cookie carries the account key and security
stamp, and every request with a cookie re-reads the account and rejects a changed stamp, a disabled or deleted
account, or a cookie from before accounts existed (`src/FiGet.Web/FiGetApp.cs:1390-1407`). Password, role and
disabled changes move the stamp. The first administrator is created only when no account exists and must
change the password before any page works; the recovery setting only comes from configuration, resets one
named account, and warns on every start (`FiGetApp.cs:460-476`). An admin cannot grant themselves super-admin:
`SetRoleAsync` refuses the actor's own account and any role the actor cannot manage
(`src/FiGet.Application/Accounts/AccountService.cs:203-230, 319-344`), the users page offers only assignable
roles, and the last enabled super admin cannot be demoted, disabled or deleted. Tested in `AccountTests`.

**S1.1 Timing oracle for user-name enumeration. Low. Confirmed.**
`AccountService.cs:57,65`: an unknown user name is answered with `hasher.Verify(hasher.Hash(decoy), password)`.
The `Lazy` caches only the random text; `Hash` runs on every call, so the unknown-user path does one PBKDF2 hash
plus one verify. Measured: 96 ms against 50 ms for a wrong password (ratio 1.9;
`FiGet.Unit.Tests.ReviewProbeTests.An_unknown_user_costs_the_same_time_as_a_wrong_password`). Fix: compute
the decoy *hash* once in the `Lazy` and verify against it. No client effect.

**S1.2 Lockout counter lost updates. Medium. Confirmed.**
`AccountService.cs:77-85` reads `FailedSignIns`, adds one in memory, and `EfUserStore.UpdateAsync`
(`src/FiGet.Infrastructure/Persistence/Stores/EfUserStore.cs:63-81`) writes the in-memory value back with
`ExecuteUpdate`. Overlapping attempts all write "previous + 1". Thirty wrong passwords sent at once left
`FailedSignIns = 1` and no lock (`ReviewProbeTests.Overlapping_wrong_passwords_still_lock_the_account_at_five`).
Preconditions: none beyond a user name; the per-address sign-in limit (20 a minute) still applies, so an
attacker needs several addresses or a burst inside one minute. Impact: the five-attempt lockout is not a
guarantee. Fix: increment atomically (`SetProperty(u => u.FailedSignIns, u => u.FailedSignIns + 1)`), read the
value back, and set `LockedUntilUtc` when it reaches the maximum; or add a concurrency token to `Users`. No
client effect.

**S1.3 Locked-out and disabled answers reveal that a name exists. Low. Plausible.** `SignInStatus.LockedOut`
is returned before the password is checked (`AccountService.cs:69-72`), so after five attempts an attacker learns
the name is real. Accepted by the design ("does not say whether the user exists" holds only for the first five).
Fix if wanted: count and lock unknown names per address too, or answer `Invalid` for locked accounts.

**S1.4 An account that must change its password can still read feeds through its cookie.** Informational.
The redirect middleware skips protocol paths (`FiGetApp.cs:269-281`) and `FeedAccess` accepts the cookie for
reads. Intended for the first admin; worth a line in `docs/auth-plan.md`.

### 2.2 OpenID Connect providers stored in the database

**Holds.** Authorization code flow with PKCE, `response_mode=query`, claims from the user-info endpoint, HTTPS
metadata required unless the issuer is loopback (`src/FiGet.Web/SignIn/OidcSchemes.cs:144-206`). The provider's
answer lands in a separate ten-minute external cookie, never the session cookie (`OidcSchemes.cs:43-50`), and
`/account/external/complete` makes the account decision (`src/FiGet.Web/SignIn/SignInEndpoints.cs:88-148`). A
new identity is never joined to an existing account; a name or email clash refuses without creating anything
(`src/FiGet.Application/Accounts/ExternalAccountService.cs:60-95, 134-143`). Linking is a challenge whose
properties carry the signed-in actor's key and are checked against the cookie at completion
(`SignInEndpoints.cs:109-126`); unlinking the last way in is refused. Group mapping never grants a role; a
provider refresh replaces only its own rows. The redirect URI is built from `PublicBaseUrl` on both legs and
tested (`ExternalSignInBehindProxyTests`). Client secrets are Data-Protection-encrypted with a purpose string
and never shown again; a secret that cannot be decrypted reads as "unreadable, enter again". Starting a sign-in
is a POST with the page's antiforgery token, so no other site can start one. Return URLs are checked with the
`/\` case covered (`SignInEndpoints.cs:214-217`).

**S2.1 Redirect URI without `PublicBaseUrl` follows the request host.** Low. Plausible. With `PublicBaseUrl`
empty, the redirect URI is `{scheme}://{host}/signin-oidc/{slug}` from the request, which behind a proxy that
rewrites `Host` is the internal name (see S10.3 for why the switch does not fix this). Fix: require
`PublicBaseUrl` when any provider is enabled, or warn on the providers page when it is empty.

**S2.2 Unverified email.** Low. Plausible. `IdentityFrom` takes `email` without `email_verified`
(`SignInEndpoints.cs:170`). Email is only used to refuse a clash and to label a link, so the worst case is an
attacker whose provider lets them set a victim's address blocking the victim's own first sign-in ("matches an
existing account"). Fix: ignore `email` when `email_verified` is present and false.

**S2.3 No allow-list: any account at the provider becomes a FiGet account. Medium. Plausible.**
Build plan section 8 and section 9 say an email allow-list "must exist" because Google sends no groups;
`docs/auth-plan.md` and `ProviderInput` dropped it. With Google, a multi-tenant Entra registration, or any
provider that lets people self-register, everyone on the internet gets a user-role account. What that account
can do: browse whatever is anonymous-readable, hold personal keys, and, because `FeedAccess` and the page
middleware exempt signed-in requests from the anonymous rate limit (`src/FiGet.Http/FeedAccess.cs:83-90`,
`FiGetApp.cs:285-311`), make unlimited requests. Fix: per-provider `AllowedEmailDomains` and `AllowedEmails`,
plus a provider switch "create accounts for new identities" that defaults to off so a super admin pre-creates
or links accounts. Confirm with `ExternalSignInTests`' fake provider issuing an unknown subject with an outside
domain. No client effect.

**S2.4 `http://` issuer accepted at save time.** Low. `ProviderInput.Problem()` (`src/FiGet.Web/SignIn/ProviderInput.cs:66-70`)
allows `http` for any host while the handler requires HTTPS metadata for non-loopback, so the failure surfaces at
the first sign-in. Fix: apply the same loopback rule in `Problem()`.

**S2.5 The request log writes the authorization code.** Low. Plausible. With `FiGet:Logging:Requests` on, the
callback `/signin-oidc/{slug}?code=...&state=...` is logged with its query
(`src/FiGet.Web/Logging/RequestLogMiddleware.cs:42-53`). The code is single-use and bound to PKCE, so exposure is
short-lived, but logs travel further than the database. Fix: mask the query on `/signin-oidc` and `/account/external`.

**S2.6 `OidcSchemes.FindEnabled` queries `FiGetDbContext` directly** (`OidcSchemes.cs:79-90`) instead of the
`IOidcProviderStore` port. Web may reference Infrastructure, so no test fails, but it is the one place in the
host that bypasses a port. Architecture note, not a security gap.

### 2.3 API tokens and personal keys

**Holds** (`src/FiGet.Application/Tokens/AccessTokenService.cs`). Secrets are 32 random bytes, base64url, prefixed
`figet_`, stored as SHA-256 (fine for high-entropy secrets), shown once. Revocation and expiry are checked on every
validation; a personal key whose owner is disabled or gone validates as nothing (`:136-168`). The ceiling holds
twice: at creation a personal key limited to a feed is refused above the owner's level there, admin scope is
refused on personal keys, only admins create service tokens and only super admins ones with admin scope
(`:78-109`); at use `AllowsAsync` takes the lower of the key's limits and the owner's level now (`:192-203`).
Header parsing accepts the four documented forms, ignores the Basic user name, guards base64 decoding and
de-duplicates candidates (`src/FiGet.Http/FeedAccess.cs:178-231`). Refused keys are audited by name without the
secret, and a garbage Basic password (possibly a person's password) is not recorded at all. Tested in
`FeedPermissionTests.Keys` and `AuditTrailTests`.

**S3.1 Twelve characters of an operator-chosen bootstrap token are stored and shown. Low. Confirmed by reading.**
`AccessTokenService.cs:125`: `EnsureAsync` stores `secret[..12]` as the prefix, and the tokens page prints it.
Minted tokens also show twelve characters, but six of them are the fixed `figet_` and the rest is six of 43
random ones; the bootstrap token has no fixed part, so twelve of a 20-character secret is most of it. The
compose example generates 64 hex characters, where it does not matter. Fix: store at most six characters for
`EnsureAsync`, or a label. `ReviewProbeTests.The_bootstrap_token_prefix_...` shows the stored prefix is twelve
characters (it passes because its threshold was set to the minted length).

**S3.2 Bootstrap token strength is the operator's.** Informational. A short `BootstrapAdminToken` is a
SHA-256-hashed admin credential with no rate limit once valid. Document a minimum (32 random bytes) and refuse
shorter values at start.

### 2.4 Authorisation

**Holds.** `FeedAccess.ResolveAsync` (`src/FiGet.Http/FeedAccess.cs:45-129`) lets a cookie through only when the
required scope is Read and no token was presented (`:94-100`), so a push, delete or upload never rides on a
browser cookie; tested by `A_signed_in_browser_cannot_push_with_its_cookie`. Anonymous read is a per-feed flag
applied only to Read. Asset directories and package feeds are separate kinds and each surface refuses the other
(`:49-52`, tested). The admin group requires authentication, checks antiforgery on every non-GET, and refuses any
endpoint that carries neither `RequiresFeedLevel`, `AdminOnly` nor `AnyAccount` (`FiGetApp.cs:542-575`), so an
unannotated endpoint fails closed; feed-scoped endpoints look the feed up by route name, answer 404 below Read
and 403 below the required level. Every form on every page carries `<AntiforgeryToken />` or is a Blazor
`EditForm`, whose binding enforces the token; the two script-driven uploads carry it as a header (tested in
`AdminUiTests` and `AssetDirectoryTests`). `FeedAdminPage.LoadFeedAsync` gates every settings page on Manage, on
the feed's kind, and on admin for naming and deletion (`src/FiGet.Web/Components/Pages/Admin/FeedAdminPage.cs:54-72`).

**Alternate names.** `EfFeedStore.FindAsync` resolves an alias to the same `Feed` row
(`src/FiGet.Infrastructure/Persistence/Stores/EfFeedStore.cs:10-21`), and everything downstream keys on
`Feed.Key`: permissions, tokens (`FeedKey`), storage folders, packages. Audit entries record the canonical name
and, for alias use, the alias in the detail (`FeedAccess.cs:136-150`). The admin filter and the settings pages
accept an alias in the route and resolve it the same way. `FeedRenameTests` cover packages, files, the swap back
and removal. Sound. The one gap is the known one: uniqueness across `Feeds` and `FeedAliases` is a check in the
store, not an index, so two admins racing can create a feed and an alias of one name
(`EfFeedStore.cs:235-238`). Fix later with a single `Names` table or a database-level check.

**S4.1 `Back()` accepts `/\host`.** Low. Plausible, not exploitable today. `FiGetApp.cs:1377-1383` rejects
`//` but not `/\`, which browsers treat as `//`; the sign-in page and the external sign-in reject both. Every
`returnUrl` the admin endpoints receive is a hidden field the page fills from its own path, so an attacker
cannot supply one without the antiforgery token. Fix: one shared `LocalOrRoot` helper for all three sites.

**S4.2 Manage level is broad for what an upstream form can do.** See S5.2 and S5.3: a Manage grant on one feed
is treated by the design as "that feed's settings", but through upstreams it reaches the server's own
environment and network. The findings below propose limits that keep Manage useful.

### 2.5 Outbound requests

**Holds: fetch by URL.** `HttpRemoteFileSource` (`src/FiGet.Infrastructure/Assets/HttpRemoteFileSource.cs`)
checks the address at connect time for every connection, refuses any name that resolves to a mix of public and
private addresses, refuses link-local and the NAT64 and 6to4 encodings of it even when private networks are
allowed (`:204-263`), follows redirects itself one hop at a time so each hop is checked (`:114-137`), refuses a
proxy without a host allow-list and checks the host of every hop against it (`:102-119, 178-198`). The size limit
applies while storing, so a large answer leaves nothing behind. Tested in `AssetTransferTests`,
`RemoteFetchProxyTests` and `RemoteFetchAddressTests`.

**S5.1 The IPv4 reserved ranges checked are the common ones.** Informational. `192.0.0.0/24`, `192.0.2.0/24`,
`198.18.0.0/15` and `198.51.100.0/24` are not refused. None hosts a metadata service; listing them costs four lines.

**S5.2 Upstream URLs are not subject to the address guard. Medium. Plausible.**
`NuGetUpstreamClient.Repository` (`src/FiGet.Infrastructure/Upstream/NuGetUpstreamClient.cs:242-258`) hands the URL
a feed manager typed to NuGet's client with `AllowInsecureConnections = true`. The client fetches the service
index and follows whatever that document names. Scenario: a Manage grant on one feed, an upstream URL of
`http://169.254.169.254/...` or an internal admin port; the first listing on that feed makes the server request it.
What comes back is not shown to the manager beyond a log line, so this is blind SSRF, but combined with S5.3 the
request also carries a credential of the manager's choosing. Fix, in order of cost: require `https` for
non-loopback upstream URLs; at save time resolve the host and apply `HttpRemoteFileSource.IsAllowed` (rebinding is
possible but the guard raises the bar); and make adding or editing an upstream's URL and credential admin-only,
leaving allow and deny lists, order and enabling to Manage. Confirm: create an upstream pointing at a stub on
loopback with the default settings and observe the request arrive. No client effect.

**S5.3 `CredentialRef` reads any environment variable and sends it to the upstream URL. High. Confirmed.**
`NuGetUpstreamClient.cs:262-270` reads `Environment.GetEnvironmentVariable(credentialRef)` for whatever name the
upstream row holds; `FiGetApp.cs:1292` stores the name straight from the form; the add and edit endpoints are
Manage level (`FiGetApp.cs:616, 1192`). Scenario: a feed manager sets `Url` to a server they control and
`CredentialRef` to `FiGet__Database__ConnectionString`, `FiGet__Auth__BootstrapAdminToken`,
`FiGet__Auth__Recovery__Password`, `OTEL_EXPORTER_OTLP_HEADERS` or a cloud credential, then opens the feed. The
server answers 401 with a Basic challenge, and the NuGet client resends with the variable's value as the
password. Demonstrated: a loopback stub received `Authorization: Basic base64("figet:" + value)`
(`ReviewProbeTests.An_upstream_credential_reference_cannot_name_an_arbitrary_environment_variable`; twelve
requests, one carrying the secret). Impact: the bootstrap admin token or the connection string gives full
control of the instance. Fix: accept only names with an allow-listed prefix, `FIGET_UPSTREAM_` (configuration
`FiGet:Connector:CredentialPrefix`), refuse anything else at save time and at read time; and make the credential
field and the URL admin-only, or drop the stored `CredentialRef` whenever a non-admin changes the URL, so a
manager can never point an existing credential elsewhere. Seeded upstreams from `FiGet:Feeds` keep working
under the prefix rule; document the rename. No client effect.

### 2.6 Input handling

**Holds.** Nupkg parsing goes through `NuGet.Packaging` (`src/FiGet.Infrastructure/Packages/PackageIndexer.cs`):
id validated with `PackageIdValidator` and capped at 100, normalised version capped at 64, symbol packages
identified by package type, and every parser exception mapped to a 400. Uploads are buffered to a temporary file
that deletes itself, with the multipart body limit and a byte-counted copy limit (`src/FiGet.Http/PackageUpload.cs`).
The `$filter` grammar is closed: unknown properties and functions are 400 at parse time, string literals and
numbers are parsed by hand, no regular expressions (`src/FiGet.Protocol.V2/ODataFilter.cs`). Asset paths refuse
`.`, `..`, backslashes, control characters, empty segments and over-long paths in one place
(`src/FiGet.Domain/Assets/AssetPath.cs:316-347`), and stored files never carry a user name: blobs are hex ids in a
fan-out folder (`src/FiGet.Infrastructure/Storage/FileSystemAssetStorage.cs:195-225`). Archive import parses every
entry name as an asset path (so `..` fails rather than resolves), counts bytes actually read against both the
per-file and the whole-archive limit, refuses links and devices in tar, and spools a zip to a temporary file under
the import limit (`src/FiGet.Application/Assets/AssetArchiveService.cs:37-93, 212-263`). Download file names come
from validated package ids and normalised versions; `Results.File` encodes the asset export name. Razor encodes
everything it renders; the one `MarkupString` in the tree encodes before inserting `<wbr>`
(`src/FiGet.Web/Components/Shared/Display.cs:44-47`, tested), and the page script uses `textContent` only.
Package URLs render as links only with `http` or `https` schemes. Theme packs are files an operator placed on disk;
a pack's logo is served only when the pack names it, from its own directory, with a sandbox CSP
(`src/FiGet.Web/Theming/ThemeService.cs:140-158`, `FiGetApp.cs:343-354`). Sign-in return URLs reject `//` and `/\`.

**S6.1 Nuspec entry read into memory without a cap. Low. Plausible.** `PackageIndexer.cs:103-119` copies the
nuspec zip entry into a `MemoryStream` with no limit; deflate expands about 1,000:1, so a 256 MB package can
declare a multi-gigabyte nuspec. Reachable by anyone with Publish, and by an upstream, because cache fill uses the
same indexer. Fix: wrap the entry in a counting stream with a few megabytes' limit and throw
`InvalidPackageException`. Confirm with a hand-built zip whose `.nuspec` entry is large and highly compressible.

**S6.2 Unbounded recursion in the `$filter` parser. High. Confirmed.** `ODataFilter.cs:416-540`: each
parenthesis level costs five frames (`ParseOperand` → `ParseOr` → `ParseAnd` → `ParseUnary` → `ParseComparison`),
and `not` recurses in `ParseUnary` (`:438`). On a 1.5 MB thread stack the parser overflowed after 1,388 levels;
Kestrel's default 8 KB request line leaves room for 4,000. A `StackOverflowException` ends the process; on
OpenShift the pod restarts and one request a second keeps it down. Precondition: any feed the caller may read,
including anonymous-read ones, on `Packages()`, `Search()` or `FindPackagesById()`
(`ReviewProbeTests.A_deeply_nested_filter_is_refused_rather_than_overflowing_the_stack`, which crashes the runner
with the trace). Fix: a depth counter in `ODataParser` incremented in `ParseOperand` for `(` and in `ParseUnary`
for `not`, throwing `ODataFilterException` past 32; and a total length cap on `$filter` and `$orderby`. Real
clients nest two or three levels (`docs/protocol-v2.md`), so no client effect.

**S6.3 Symbol package PDBs are read whole and kept together.** Low. Plausible.
`src/FiGet.Application/Packages/PackageIngestionService.cs:130-160` allocates `entry.Length` bytes per PDB (cap
512 MB each) and holds every PDB until the loop ends; a 256 MB upload can hold several 512 MB PDBs. Publish
rights required. Fix: stream each PDB to storage, cap the total.

**S6.4 Asset metadata content type is stored unvalidated.** Low. Plausible.
`src/FiGet.Application/Assets/AssetService.cs:383` stores whatever string the metadata call sends; over 256
characters is a `DbUpdateException` and a 500, a value with a line break fails when set as a header. Fix: parse
with `MediaTypeHeaderValue.TryParse` and cap the length; see also S6.7 for what the type is allowed to be.

**S6.5 XML external entities in nuspec.** Not verified. `NuspecReader` is NuGet's own and, to my knowledge,
loads with DTD processing prohibited and no resolver; I did not read the library. A unit test that pushes a nuspec
with a `DOCTYPE` naming a local file would settle it in a minute.

**S6.6 YAML theme packs.** Not verified beyond the loader's shape. `YamlDotNet` with `IgnoreUnmatchedProperties`
and no tag mappings deserialises into plain DTOs, which is the safe configuration. Packs come from the operator's
disk only, and `customCSS` and `fontUrl` are inserted into the stylesheet verbatim by design; a pack is trusted
like a configuration file.

**S6.7 Asset downloads run as pages on FiGet's origin. High. Confirmed.**
`src/FiGet.Protocol.Assets/AssetEndpoints.cs:59-84` serves the blob with the stored content type inline, with no
`Content-Disposition`, no `Content-Security-Policy` and no `X-Content-Type-Options`; the type is whatever the
uploader sent (`AssetContentTypes.Resolve`) or later set through metadata (S6.4), and `X-Source-Url` fetches keep
the remote server's type. Demonstrated: a `page.html` uploaded with `text/html` came back inline as `text/html`
with none of the three headers (`ReviewProbeTests.An_html_asset_is_not_served_as_a_page_on_the_site_origin`).
Scenario: anyone with Publish on any asset directory, or an archive import, uploads an HTML or SVG file; the
browse page links straight to the content URL; an admin who opens it runs the attacker's script with the admin's
cookie. The cookie is `HttpOnly`, but the script can load any admin page, read its antiforgery token and post any
admin form: grant itself Manage everywhere, create service tokens, change feed settings. Fix, all three: send
`X-Content-Type-Options: nosniff` on every asset response; send `Content-Security-Policy: sandbox` (plus
`default-src 'none'`) exactly as the theme asset route already does; and send `Content-Disposition: attachment`
for every type except a short inert allow-list (raster images, plain text, PDF is not inert in every viewer).
Client compatibility: `Invoke-WebRequest`, `curl`, `win_get_url` and installers ignore these headers; the only
change a person sees is a browser downloading an HTML file instead of rendering it. Serving downloads from a
separate host name is the stronger alternative and costs a second ingress.

### 2.7 Resource limits

**Holds.** Token-bucket limits per connection address for anonymous protocol reads, pages and sign-ins, keyed by
`Connection.RemoteIpAddress` (`src/FiGet.Http/RequestRateLimits.cs:52-72`), applied to protocol requests only
after the key was checked so a garbage key does not evade it. Response sizes: `$top` capped at 1,000 and the
package scan at 2,000, v3 `take` at 1,000, registration pages of 64, package pages of at most 500 rows. The audit
queue is bounded at 10,000 with drop-oldest and the drops counted and logged (`src/FiGet.Http/AuditLog.cs:30-40`).
Retention removes at most 1,000 per run. Dependency pulls stop at 100 packages and depth 10. Regular expressions
from feed managers run with a one-second timeout and a bad pattern matches nothing
(`src/FiGet.Application/Connectors/ConnectorService.cs:711-737`).

**S7.1 The audit caller is the raw forwarded-for header. Medium. Confirmed.**
`src/FiGet.Http/RequestActor.cs:33-42` prefers `X-Forwarded-For` over the connection address, and
`docs/configuration.md:135-136` says the header is never read directly. Two demonstrations: without the switch, a
client-written value became the stored caller (`ReviewProbeTests.The_audit_caller_is_the_connection_address_...`);
with the switch on and a proxy that appends, the forwarded-headers middleware consumed the proxy's entry and the
stored caller was the client-written remainder (`ReviewForwardedHeadersProbeTests.With_the_switch_on_a_client_...`).
So in the documented deployment every "from" column in the audit log is attacker-chosen. The same value keys the
throttles for `token.refused` and `feed.alias.used` (`FeedAccess.cs:140, 170`), so varying the header defeats
the once-per-window rule and floods the queue and the table. Fix: use `Connection.RemoteIpAddress` in
`RequestActor.Caller`, as `RequestRateLimits` does, and keep the raw header only in the request log where it is
labelled as raw. No client effect.

**S7.2 Rate limits are per replica.** Low. Plausible. Each replica holds its own buckets, so a client spread over
N replicas gets N times the rate. Acceptable; document it, or move the counter to the database when phase 6
adds a lease store.

**S7.3 Upstream search fan-out.** Low. Plausible. A v3 search with `take=1000` on a proxy feed asks each upstream
for up to ten pages of 100 (`ConnectorService.cs:417-461`, `NuGetV3Endpoints.cs:322, 375`); anonymous, and
repeated at the anonymous rate. Fix: cap the upstream share at 100 hits and two chunks per upstream, and cache
upstream search answers for a minute keyed by query.

**S7.4 Every unknown id costs a catalogue row and an upstream round-trip. Medium. Confirmed.**
`ConnectorService.CatalogAsync` (`:761-836`) saves the answer for any id the upstream answered, including an empty
one (`:818, 824`), and `EfUpstreamIndexStore.SaveAsync` inserts a row for it
(`src/FiGet.Infrastructure/Persistence/Stores/EfUpstreamIndexStore.cs:53-114`). Three made-up ids left three rows
(`ReviewProxyProbeTests.An_id_no_upstream_holds_leaves_no_catalogue_row_behind`). At the default anonymous rate that
is 1.7 million rows a day, each up to 128 characters, on any anonymous-read proxy feed; and each id is a request
to the gallery, which may throttle the server's address. The look-through download path adds a download attempt
per upstream for an id no upstream lists (`ConnectorService.cs:323-350`). Fix: keep a short-lived in-memory
negative cache and do not persist empty catalogues, or persist them with their own short TTL and a periodic
prune; skip the download attempt when a fresh authoritative catalogue does not list the version.

**S7.5 The retention job's deletions.** Holds. `RetentionPolicy.Plan` is pure and shared with the preview, always
keeps the newest version of a package, protects versions used within the window, unlists rather than deletes on
unlisting feeds, and never touches pushed versions when pruning the cache (`src/FiGet.Application/Packages/RetentionPolicy.cs`).
Runs are capped at 1,000 removals and delayed five minutes after start. Tested in `RetentionPolicyTests` and
`RetentionTests`. Every replica runs it hourly; the second run finds nothing, so it is waste, not damage.

### 2.8 Data at rest

**S8.1 Data Protection keys unencrypted in the database. Medium. Plausible (owner-known).**
`FiGetApp.cs:152-154` persists the key ring to `DataProtectionKeys` with no `ProtectKeysWith*`, and the start-up
log says so, which was known and accepted for the provider secrets. The larger consequence: the sign-in cookie and the
antiforgery cookie are protected with the same ring, and the security stamps are in the same database, so read
access to the database (a backup, a reporting login, a leaked connection string, S5.3) forges a super-admin
cookie without any password. Fix: `ProtectKeysWithCertificate` from a mounted secret, or a small `IXmlEncryptor`
over AES-GCM with a master key from `FiGet:DataProtection:MasterKeyRef`; refuse to start without one when the
environment is not Development. The ring stays in the database, which is right for replicas. No client effect.

**S8.2 Secrets in logs and audit entries.** Holds, with two edges. Audit details never contain a token; refused
keys are named, not printed; the sign-in page records the user name only. Edges: the request log's query string
on the OIDC callback (S2.5), and `upstream.update` records `credentialRef=` by name, which is fine once S5.3
restricts names. `RemoteFetchSettings.Proxy` may carry credentials in the URL and the proxy URI is never logged.
`PublicUrls` never embeds credentials. The recovery password is read from configuration and never written
anywhere.

**S8.3 Package and asset hashes are stored, never used for trust.** Informational. MD5 and SHA-1 are reported for
compatibility, SHA-256 is the ETag; nothing decides anything on them.

### 2.9 Storage

**Holds.** Both `SafePath` implementations refuse empty segments, `.`, `..`, separators, colons, invalid
characters and upper case, then check the resolved path starts under the root
(`src/FiGet.Infrastructure/Storage/FileSystemPackageStorage.cs:96-117`, `FileSystemAssetStorage.cs:204-225`); blob
and upload ids must be 32 lower-case hex characters. Writes go to a temporary file beside the target and are moved
into place, so a reader never sees a partial file on a shared volume. The start-up move handles the "already
moved by another replica" case and merges without overwriting when the target exists
(`src/FiGet.Infrastructure/Storage/StorageLayout.cs:35-115`, unit-tested).

**S9.1 Two replicas starting at once can crash on the one-time move.** Low. Plausible. `StorageLayout.cs:96`
catches only `DirectoryNotFoundException`; when replica B checks that the target does not exist, A moves, and B's
`Directory.Move` then fails because the target exists, that is an `IOException` and the process exits. One start-up
of one release, and the second start succeeds, so this is a note for the phase 6 rollout rather than a fix.

**S9.2 Delete ordering and orphans.** Informational, consistent with the code comments. A version delete removes the
row, then the files (`PackageIngestionService.cs:264-269`); an asset delete removes rows, then blobs, logging a
blob it could not remove; a feed delete removes files, then rows. Each choice leaves an orphan on the failure side
it chose, which is the right side in every case. What is missing is anything that finds orphaned files later: an
asset replaced by two concurrent uploads leaks the loser's blob (`AssetService.cs:136-171`), and a failed
`DeletePackageAsync` leaves a folder no row names. Fix: a `figet verify` or a periodic sweep in phase 6 that lists
files without rows.

**S9.3 Windows reserved names.** Informational. A package id `con` or `nul` is a valid NuGet id and an invalid
Windows folder; the container is Linux, so this only touches a developer running on Windows.

### 2.10 Response headers and cookies

**Holds.** The session cookie is `HttpOnly`, `SameSite=Lax`, eight hours sliding; the external cookie ten minutes.
Antiforgery is the framework's. The theme asset route sends a sandbox CSP and `nosniff`. Package downloads are
compressed by neither the app nor the BREACH-relevant path, and the compression reasoning at `FiGetApp.cs:184-190`
is right for the paths it covers.

**S10.1 No security headers on pages.** Low. Plausible. No `Content-Security-Policy`, `frame-ancestors`,
`X-Content-Type-Options` or `Referrer-Policy` on any page, and `App.razor` carries an inline theme script
(`src/FiGet.Web/Components/App.razor:21-32`) that a CSP would need to nonce. Framing is the practical one: the
admin pages can be framed by any site for click-jacking. Fix: a middleware for non-protocol paths adding
`Content-Security-Policy: frame-ancestors 'none'` (or `X-Frame-Options: DENY`), `nosniff` and
`Referrer-Policy: strict-origin-when-cross-origin`; a full CSP with a nonce later. HSTS belongs on the proxy.

**S10.2 Cookies have no explicit secure policy.** Low. Plausible. `FiGetApp.cs:160-168` and `OidcSchemes.cs:43-50,
180-182` leave `SecurePolicy` at `SameAsRequest`; behind a proxy that terminates TLS and does not forward the
scheme, cookies are issued without `Secure`. Fix: `CookieSecurePolicy.Always` when `PublicBaseUrl` starts with
`https`, and the same for correlation and nonce cookies.

**S10.3 The forwarded-headers switch does not do what the documentation says, and trusts every peer. Medium. Confirmed.**
`docs/configuration.md:12` says `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` makes `X-Forwarded-Proto` and
`X-Forwarded-Host` honoured. The framework's switch enables `For` and `Proto` only; with the switch on, a request
carrying both headers produced a service index with `https://127.0.0.1:port/...`
(`ReviewForwardedHeadersProbeTests.With_the_switch_on_the_forwarded_host_is_used_in_protocol_urls`). So with
`PublicBaseUrl` empty and a proxy that rewrites `Host`, every absolute URL in every protocol answer, the OIDC
redirect URI and the copy boxes carry the internal name. The same switch clears the known-proxies list, so any
peer that can reach the pod without the proxy in front chooses its own address for the rate limiter and the audit
log. Fix: correct the documentation (`PublicBaseUrl` is required behind a proxy; the switch is for the client
address only), and in phase 6 set `ForwardedHeadersOptions.KnownNetworks` to the ingress range or restrict ingress
with a NetworkPolicy. `AllowedHosts` is `*` (`src/FiGet.Web/appsettings.json:9`), so with `PublicBaseUrl` empty the
`Host` header is also reflected into protocol URLs; setting `PublicBaseUrl` closes both.

---

## 3. Architecture findings

### 3.1 Layering

The boundary holds in practice, not only in the test. Project references are exactly the ones the table in
`CLAUDE.md` allows: Domain references `NuGet.Versioning` only; Application references Domain and logging
abstractions; Http and the four protocol projects reference Domain, Application and Http; only Web references
Infrastructure and the two migration assemblies. Ports are interfaces in `FiGet.Application/Ports` with one EF or
filesystem implementation each, bound in `FiGetApp.ConfigureServices`. Application services take ports only;
`EfUpstreamDescriptionStore` takes `DbContextOptions` to open its own context for batched writes, which stays
inside Infrastructure.

Two remarks. `LayerBoundaryTests` asserts on Domain and Application only; Http and the protocol projects are
held by their project files but not by the test, so an EF reference added to `FiGet.Http` would build and pass.
Extend the test to those five assemblies (forbid `Microsoft.EntityFrameworkCore`, `NuGet.Protocol`,
`NuGet.Packaging`). And `OidcSchemes.FindEnabled` (`OidcSchemes.cs:79-90`) reads `FiGetDbContext` directly from the
host; it is legal for Web and it exists to avoid a scoped port in a singleton, but it is the only such place and
worth a comment or a `IOidcProviderStore` resolved through a scope.

### 3.2 Behaviour with several replicas

Sessions and antiforgery are replica-safe because the key ring is in the database (once S8.1 encrypts it).
Provider schemes are built per replica from the row's `UpdatedUtc`, so a change on one replica reaches the others
at the next sign-in with no restart. Static rendering means no affinity.

In-memory state, per replica:

| State | Effect of N copies | Verdict |
|---|---|---|
| `UpstreamMetadataCache` (descriptions) | N copies of the same text, each bounded at 500 ids; correctness lives in the database | Fine by design |
| `UpstreamRefreshQueue.inFlight` and the refresh loop | Each replica walks the same stale catalogue once; N walks of several megabytes per package | Acceptable now; a lease in phase 6 halves the cost |
| `AuditLog.throttled` | Throttle windows multiply by N | Fine |
| `RequestRateLimits` | Limits multiply by N | Document (S7.2) |
| `NuGetUpstreamClient.repositories` | Keyed by key, URL and credential, so an edit takes effect on every replica at its next read; old entries are never evicted | Fine; a leak only after many edits |
| `ThemeService.packs` | "Reload packs" acts on one replica; the others read the directory at start | Note for the appearance page: say so, or read the directory's mtime |
| `AuditWriterService.lastPrune` | Each replica prunes every six hours | Fine |

Background services run on every replica: retention (hourly), upload sweep (hourly), audit writer and prune,
catalogue refresh. All are idempotent: `PurgeAsync` and `SetListedAsync` find nothing the second time, the sweep
removes an upload only once, and the audit writer stores what its own replica queued. The cost is duplicate work
and duplicate `retention.run` audit entries, not corruption.

Start-up on every replica: migrations under EF Core's migration lock (fine), seeding through `CreateAsync` with the
unique-index race caught, the bootstrap token with the race caught, the first admin with the race caught, and the
folder move with one race not caught (S9.1). `ExpectedReplicas` above one with SQLite refuses to start, which is an
honour-system guard; a `Sqlite` provider with a shared volume and two replicas is still possible if the setting is
left at one.

### 3.3 Consistency of files against rows

The invariants are stated in comments and consistent per operation (S9.2). Two things are worth adding before
phase 6: a sweep that reports files without rows, and a note that `AddVersionAsync` with overwrite deletes and
re-inserts the version row inside a transaction before the file is replaced, so for the duration of the file
copy the row's hash and size describe the new file while the old bytes are still served; a client that compares
`PackageHash` against what it downloaded during that window sees a mismatch once and retries. Harmless, worth a
line in `docs/protocol-v2.md`.

The name uniqueness gap across `Feeds` and `FeedAliases` is known and documented in the store; the fix is a
`Names` table (name, kind, feed key) with the unique index, populated by both paths.

### 3.4 Proxy connector correctness

Read against `docs/protocol-v2.md` and build plan section 5.

- **Ownership.** `DecideOwnerAsync` (`ConnectorService.cs:603-645`) walks enabled upstreams in ordinal order,
  skips ids the allow and deny lists exclude, owns at the first upstream that lists a version, holds an
  unlisted-only upstream as a fallback owner, and returns undecided when an upstream ahead of any owner could not
  be asked and nothing is remembered. `EnsureCachedAsync` fetches from the owner only, or from nobody while
  undecided (`:314-326`). Sound, and every branch has a test in `ProxyFeedTests`.
- **Merged list and single latest.** `VersionListBuilder.Build` (`src/FiGet.Domain/Versions/VersionListBuilder.cs:43-88`)
  de-duplicates by `NuGetVersion`, local wins over upstream, and computes both latest flags over the merged,
  listed set with `ReferenceEquals`, so each flag is true on at most one entry. Every listing endpoint goes through
  it: v2 `RowsForIdAsync` and `SearchRowsAsync`, v3 `MergedAsync`, search and autocomplete, the management API.
  The v2 `Search()` filters listed and prerelease *after* the merge, so an unlisted or prerelease latest cannot
  leak into a stable search. Sound.
- **Withdrawal.** Reconciliation runs only on an authoritative answer and needs positive evidence to re-list
  (`:182-231`); a stored catalogue written before the facts columns existed reads as "no news". Tested three ways.
- **Two small divergences.** `StoredUpstreamCandidatesAsync` (`:243-293`), used by listing pages, works from stored
  catalogues and skips an upstream nothing is stored for, so a feed page can show a lower upstream's package for an
  id whose higher upstream has simply never been asked, while the package page, asking live, answers "undecided"
  and serves nothing. Cosmetic until someone clicks, and then the live rule wins. And `SearchUpstreamsAsync` keeps
  the first upstream's hit per id in priority order, which is the owner only when the owner's search also matched;
  the click-through resolves through ownership, so the wrong description can show for a moment. Both are display
  only.
- **Hash verification on cache fill** (build plan phase 5) is not implemented; the v3 client library does not expose
  a hash, the v2 one does. Combined with `AllowInsecureConnections`, a plain-HTTP upstream is a package injection
  path for whoever sits on that network. Require `https` (S5.2) and it is moot for most fleets.

### 3.5 Phase 6 readiness

What is ready: the storage ports are stream-shaped and keyed by feed key and id, folders are per key so a rename
moves nothing, uploads are buffered outside storage, the key ring is in the database, health endpoints exist, the
container runs under an arbitrary UID, and every background job is idempotent.

What will be costly to change later, in descending order:

1. **Range requests on downloads.** Every download is `Results.File(stream, enableRangeProcessing: true)` or
   `Results.Stream`, which needs a seekable stream or a known length. An S3 object stream is neither, so either the
   S3 implementation buffers every download to a temporary file (a gigabyte installer on every replica) or the
   ports grow a `(offset, length)` overload now and the endpoints pass the parsed `Range` through. Change the port
   before writing the S3 adapter.
2. **Secrets model.** `CredentialRef` is "an environment variable name"; on OpenShift the natural source is a
   mounted secret file. Decide now: prefix-limited environment variables (S5.3) plus `file:` references under a
   fixed mount, resolved by one `ISecretSource` port that the OIDC client secret could also use instead of the
   database.
3. **Jobs on one instance.** Add a `Leases` table and an `ILeaseStore` port (take, renew, release by name with a
   TTL, one `ExecuteUpdate` compare-and-set) and wrap retention, the audit prune and the upload sweep in it. Ten
   lines each; the `CronJob` mode in the build plan becomes `figet jobs run <name>` calling the same code.
4. **The start-up folder move** (`FiGetApp.cs:411`) calls the filesystem layout directly with a path and must be
   skipped when the provider is S3; put it behind the storage port or a provider check now so the S3 adapter does
   not inherit it.
5. **Temporary files.** Upstream downloads buffer in the system temp directory (`NuGetUpstreamClient.cs:95-131`)
   rather than `Storage:TempPath`; on a pod with a small writable layer that is the ephemeral disk. Route it
   through `UploadOptions.TempPath`.
6. **Helm values that must exist:** `PublicBaseUrl` (S10.3), `ExpectedReplicas`, the master key (S8.1), the
   credential prefix, `KnownNetworks`, a `ReadWriteMany` claim or S3 settings, and the proxy body limit at least
   `MaxAssetSizeMB`.

### 3.6 Tests: where a regression slips through

The suite is strong where the protocol is concerned: fixture replay of five real clients, both database providers,
ownership and withdrawal from every angle, page rendering under concurrency. Gaps found while probing:

- Nothing asserts the audit caller, the asset response headers, the parser's depth, or that a `CredentialRef`
  name is constrained: the six probes in the review files are the missing tests, and each becomes a regression
  test once its fix lands (flip the assertions where a comment says so).
- `LayerBoundaryTests` does not cover Http or the protocol projects (3.1).
- No concurrency test on the lockout counter or on `AddVersionAsync` overwrite.
- `RateLimitTests` covers one address on one replica; nothing covers the forwarded-headers switch beyond the OIDC
  redirect URI.
- The SQL Server half is skipped without the variable; CI runs it, a developer's machine usually does not. The
  probes here ran on SQLite only.
- No test opens the theme `customCSS` path or the `Reload packs` action against a directory that changes.
- The compatibility scripts under `tests/FiGet.Compat/` are the only check on Windows PowerShell 5.1; none of the
  fixes proposed here touches the wire format, but S6.7's `Content-Disposition` should be run through
  `win_get_url` once.

---

## 4. What was checked and found sound

- Password hashing, rehash-on-upgrade, minimum length, first-admin bootstrap, recovery from configuration, role
  management rules, last-super-admin protection, security stamp re-validation on every request.
- OIDC: code flow with PKCE, HTTPS metadata, external cookie separation, no automatic linking, link bound to the
  signed-in actor, group sync scoped per provider, redirect URI from the public base URL on both legs, encrypted
  client secrets, antiforgery on the start of a sign-in, `/\` handled in return URLs.
- Tokens: hashing, prefix, expiry, revocation, owner-disabled check, the ceiling at creation and at use, header
  parsing.
- Authorisation: cookie limited to reads on protocol paths, per-feed anonymous read, kind separation, admin group
  filter with fail-closed metadata, antiforgery on every form and script upload, `FeedAdminPage` gating, alias
  resolution by key.
- Fetch by URL: connect-time address checks, per-hop redirect checks, proxy allow-list, link-local refusal
  including NAT64 and 6to4.
- Input: nupkg parsing limits, closed `$filter` grammar (except depth), asset path rules, archive import path and
  size rules with links refused, multipart part accounting, file-name construction, HTML encoding including
  `Display.BreakableId`, theme pack file serving.
- Limits: per-address token buckets keyed by the connection, page sizes, bounded audit queue with drop accounting,
  retention cap and rules, dependency pull caps, regex timeouts.
- Storage: both `SafePath` implementations, hex ids, atomic writes, the start-up move except one race.
- Protocol: v2 root aliases, the `/api/v2` probe answer, unparsed filters answered 400, reason phrases and the push
  warning header sanitised to printable ASCII, JSON and XML written by encoders, LIKE escaping in search.
- Connector: ownership, merged list with one latest, withdrawal only on authoritative answers, stale catalogues
  served and refreshed behind the request, pushed ids owning their name.
- Container: non-root, arbitrary UID, writes under `/data` and temp, publish output verified in the image.

**Not verified:** XML entity handling inside `NuspecReader` (S6.5); YAML deserialisation beyond the loader
configuration (S6.6); every probe on SQL Server; the Windows PowerShell 5.1 effect of `Content-Disposition` on
assets (S6.7); the exact Kestrel request-line limit behind the real proxy (S6.2 assumes 8 KB; larger limits make
it worse, not better); the retention job under two replicas at once (reasoned, not run); the Helm chart (does not
exist yet).

---

## 5. Proposed order

**Before publishing the repository** (an instance is already being used, and readers will find these first):

1. S5.3 credential reference prefix and admin-only URL and credential (High).
2. S6.2 parser depth limit (High; one line of state, ten lines of code).
3. S6.7 asset response headers: `nosniff`, sandbox CSP, `attachment` outside an inert allow-list (High).
4. S7.1 audit caller from the connection address (Medium; one line).
5. S1.2 atomic failed-sign-in counter (Medium).
6. S10.3 fix `docs/configuration.md` on `X-Forwarded-Host`, and make `PublicBaseUrl` the documented requirement
   behind a proxy (Medium; docs now, `KnownNetworks` in phase 6).
7. S1.1 decoy hash computed once; S3.1 shorter bootstrap prefix; S4.1 one return-URL helper; S2.5 mask the
   callback query (all Low, all small).
8. Extend `LayerBoundaryTests` to Http and the protocol projects; adopt the six probes as regression tests.

**Before phase 6** (two replicas, SQL Server, shared storage):

9. S8.1 encrypt the key ring with a master key from a secret; the cookie-forgery consequence makes this
   deployment-critical the moment the database is shared with anything else.
10. S2.3 provider allow-list and "create accounts" switch, before any public provider is enabled.
11. S5.2 upstream URL rules: `https` required off loopback, address check at save, admin-only URL edits.
12. S7.4 negative cache and no empty catalogue rows; S7.3 fan-out cap; the download-attempt guard.
13. S10.1 and S10.2 page headers and `Secure` cookies.
14. 3.5 items 1 to 5: range-capable storage port, secret source port, lease store, guard the folder move, route
    temp files; then the S3 adapter and the chart.

**Later:**

15. S6.1, S6.3 memory caps on nuspec and PDB reads; S6.4 metadata content-type validation.
16. Orphan sweep or `figet verify` (S9.2); the `Names` table for cross-table uniqueness (2.4).
17. Hash verification on v2 cache fill (3.4); host filtering (S10.3 last paragraph).
18. S1.3, S2.2, S2.4, S5.1, S9.1, the two connector display divergences (3.4), replica-aware rate limits (S7.2),
    the appearance page's per-replica reload note (3.2).

---

## 6. Follow-up (same day)

What was decided: Manage does not include an upstream's URL or credential (now admin-only; managers add known public
galleries); the email allow list was dropped by accident (restored, with an account-creation switch); the probes
became regression tests next to the code they cover; items 1 to 14 of section 5 were done, except the parts the
cluster operators' answers made unnecessary (S3 byte ranges, a secret-source port) and `KnownNetworks`, which belongs
in the Helm chart. What was built, and what was deliberately left, is in `docs/status.md` under this date and in
`docs/backlog.md` under phase 6 and the smaller review items. Two deviations from the proposals above:

- **S5.2**: no DNS resolution at save time and `http` stays allowed for admins; with URL edits admin-only, the save
  check refuses non-http schemes and link-local addresses only.
- **S7.4**: the download attempt for an id no upstream lists is kept, because a just-published package can be
  downloadable before a gallery lists it.
