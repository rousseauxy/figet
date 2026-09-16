# Configuration

Every setting FiGet reads. A key that is not in this file does not exist. Settings come from
`appsettings.json`, environment variables (`FiGet__Database__Provider`), or any other ASP.NET Core
configuration source. Never put secrets in files that are committed; use environment variables or mounted
secret files.

## FiGet

| Key | Default | Meaning |
|---|---|---|
| `FiGet:PublicBaseUrl` | empty | Absolute base URL used in every URL the protocols emit, for example `https://packages.example.org`. Empty: derived from the request's scheme and `Host` header. **Required behind a reverse proxy**: the forwarded-headers switch below corrects the scheme and the client address but not the host, so without this every protocol URL, the sign-in redirect URI and the copy boxes carry the name the proxy used to reach the container. |
| `FiGet:CompressProtocolResponses` | `true` | Brotli or gzip, at the fastest level, for XML, JSON and text answers under `/nuget` and `/api/packages` when the client sends `Accept-Encoding`. Package and symbol downloads (already zips) and browser pages are never compressed. Turn off when a reverse proxy in front already compresses. |
| `FiGet:Version` | empty | What the signed-in menu shows as the running version, for example the image tag a deployment built (`docker-1.2.3`). Empty: the assembly's informational version, or `version not set` when the build stamped none — the SDK's default `1.0.0` counts as none, so an unstamped build does not announce itself as a release. |

## FiGet:Database

| Key | Default | Meaning |
|---|---|---|
| `Provider` | `Sqlite` | `Sqlite` or `SqlServer`. |
| `ConnectionString` | empty | SQLite: empty means `Data Source={Storage:Root}/figet.db`. SQL Server: required. |
| `MigrateOnStartup` | `true` | Apply pending EF Core migrations on start. EF Core takes a migration lock, so several replicas may all do this. |
| `ExpectedReplicas` | `1` | Number of instances sharing the database. With `Sqlite` and a value above 1, FiGet refuses to start. |

## FiGet:Storage

| Key | Default | Meaning |
|---|---|---|
| `Provider` | `FileSystem` | Only `FileSystem` exists today. |
| `Root` | `data` under the content root; `/data` in the container image | Package files go under `{Root}/files`, the SQLite database (when used) under `{Root}`. On a cluster, a volume shared by all replicas (ReadWriteMany). |
| `TempPath` | system temp directory | Where uploads, the PDBs of a symbol package, and packages downloaded from upstreams are buffered while they are validated. Must have room for the largest package or asset. On a pod, point it at the storage volume or an `emptyDir`, not the container's writable layer. |

## FiGet:Feeds

A list of feeds created on start when they do not exist. Existing feeds are never changed from
configuration, with one exception: `Folder` and `FolderWrites` of an asset directory are applied on every start, because
the mount is the operator's to provide and to take away. Configuration owns folders outside the shares mount
(`Assets:SharesRoot`) and the pages own folders under it: a directory listed here without `Folder` keeps a folder
chosen on its settings page, and loses one that came from configuration. If no feed exists after seeding, a feed named
`default` is created.

What is not here is set on a feed's settings page: retention, instructions, access grants, and the addresses a feed
may be reached from (*Allowed networks*, under the settings; empty means any).

A configured feed renamed on its settings page keeps existing as long as its old name is kept as an alternate name: the
name is then taken, nothing is created, and the start logs a warning to rename it here too. Without the alternate name,
the next start creates a new, empty feed of the configured name.

| Key | Default | Meaning |
|---|---|---|
| `Feeds:N:Name` | required | Letters, digits, `.`, `-`, `_`; 1 to 64 characters; starts with a letter or digit. Case-insensitive in URLs. |
| `Feeds:N:Kind` | `Curated` | `Curated`, `Proxy` or `Assets`. A feed with upstreams is a proxy feed whatever this says. `Assets` makes an asset directory: files by path under `/endpoints/{name}`, described in `docs/protocol-assets.md`; `AllowOverwrite`, `DeletionBehavior` and upstreams do not apply to it. |
| `Feeds:N:AnonymousRead` | `false` | When true, every read endpoint works without credentials. On an asset directory: downloading a file by its path. |
| `Feeds:N:AnonymousList` | `AnonymousRead` | Asset directories only. When true, folders can be listed, exported and browsed without credentials. A consumer that knows its paths needs only `AnonymousRead`; with this off, a folder shows a stranger nothing and a wrong path is the same 404 as a right one, so listing needs a signed-in account or a key with Read. |
| `Feeds:N:Folder` | empty | Asset directories only. A folder on the server - a mounted share - that *is* the directory's content, read as it is: no copy, no row per file, a file placed on the share served at once. Any path, for the operator; applied on every start. Hidden and system files, `web.config`, `Thumbs.db`, `desktop.ini` and `~$` lock files are never listed or served, and a link leading out of the folder is refused. See `docs/protocol-assets.md`. The other way to a folder-backed directory is `Assets:SharesRoot` below, where an administrator picks a sub-folder of one mount on the pages; a directory with `Folder` here shows no chooser. |
| `Feeds:N:FolderWrites` | `false` | With `Folder`: whether uploads, new folders and deletes through FiGet act on the folder. Off, every write answers 403 and the share's own permissions decide who writes. Applied on every start. |
| `Feeds:N:Purpose` | `Any` | Package feeds only: `Any`, `PowerShell`, `NuGet` or `Chocolatey`. Sets the connect and install commands the pages show, and refuses pushes of another kind of package, judged from its files: a `PowerShell` feed takes only a module (`{id}.psd1` at the root) or a script (`{id}.ps1`); a `NuGet` feed refuses modules and Chocolatey packages (a `chocolateyInstall.ps1`, `chocolateyUninstall.ps1` or `chocolateyBeforeModify.ps1`); a `Chocolatey` feed refuses modules and .NET packages (`lib/`, `ref/`, `runtimes/`, `build/`, `analyzers/`, `contentFiles/`, or a dotnet tool, template or SDK package type). Copies from an upstream are not checked. Only used when the feed is created; afterwards it is the settings page's. |
| `Feeds:N:AllowOverwrite` | `false` | When true, pushing an existing version replaces it instead of answering 409. |
| `Feeds:N:DeletionBehavior` | `Unlist` | `Unlist` hides the version from search and keeps it downloadable; `HardDelete` removes the metadata and the files. |
| `Feeds:N:MergePushedIdsWithUpstreams` | `false` | When false, an id with a version pushed to the feed is served only from the feed: its upstreams are not asked about it, and copies of it cached from an upstream are unlisted. When true, the pushed and upstream versions are merged into one list, so the higher version of either is latest. Only right when the upstream package really is the same package. |
| `Feeds:N:Upstreams:M:Name` | `upstream-1`, `upstream-2`, … | A name for logs and the UI, unique within the feed. Left out, the upstream is named after its position in the list. |
| `Feeds:N:Upstreams:M:Url` | required with an upstream | A v3 service index (`https://api.nuget.org/v3/index.json`) or a v2 feed root (`https://www.powershellgallery.com/api/v2`, `https://community.chocolatey.org/api/v2`). |
| `Feeds:N:Upstreams:M:Kind` | `V3` | `V3` or `V2`. The URL alone cannot always tell, so it is stated. |
| `Feeds:N:Upstreams:M:Allow:X` | empty | Regular expressions on the package id. Empty allows every id; otherwise an id must match one to be listed or fetched. |
| `Feeds:N:Upstreams:M:Deny:X` | empty | Regular expressions on the package id. A match is never listed or fetched, even when it is allowed above. |
| `Feeds:N:Upstreams:M:CredentialRef` | empty | Name of the environment variable holding this upstream's API key or password. Written as `user:password`, it sends that user name, for an upstream that checks both (split at the first colon); a bare key is sent with the user name `figet`. It must start with `FIGET_UPSTREAM_` and be upper case (`FIGET_UPSTREAM_GALLERY`); any other name refuses to start. The secret itself is never stored. In the admin pages only an admin sets an upstream's URL, protocol and credential; a feed manager edits its name, patterns and switch, and adds one of the known public galleries. |

Environment variable form: `FiGet__Feeds__0__Name=modules`, `FiGet__Feeds__0__AnonymousRead=true`.

## FiGet:Connector

Applies to every proxy feed. Upstreams themselves are configured per feed, above.

| Key | Default | Meaning |
| --- | --- | --- |
| `UpstreamIndexTtl` | `00:05:00` | How old one upstream's cached catalogue for a package may get before it is fetched again. Not an expiry: a catalogue older than this is still served immediately and refreshed behind the request, so only the first ever view of a package waits for the upstream. A new upstream release becomes visible to the reader after this window, on the view that follows the refresh. |
| `UpstreamTimeout` | `00:00:30` | How long one upstream call may take before that upstream counts as unavailable for this request. Listing a package with hundreds of versions on a v2 gallery is a paged walk of several megabytes, so this is not the latency of one request. A timeout is treated as "the upstream did not answer": the last known list is served and nothing is considered withdrawn. |
| `SweepMaxIdsPerFeed` | `1000` | The most ids the catalogue sweep refreshes per feed in one run; the rest wait for the next. A warning names the feed when it is reached. The real bound is elsewhere: refreshes are fetched one at a time, and only for ids this feed already holds. |
| `MaxDescribedPackages` | `500` (`0` or less means the default) | Package ids one replica keeps upstream descriptions in memory for before dropping the oldest. The descriptions are the large part and every replica holds its own copy, so this decides the memory a busy instance settles at. Lowering it costs listings their description text until the next refresh, never their correctness. |

## FiGet:Theming

A theme pack is a YAML file of custom-property overrides, compiled once and served at
`/themes/{name}.css` after `app.css`. Every token has a default in the base stylesheet, so a pack that
sets three colours is a complete theme.

The token names and the file format are shared with the sibling applications here, so a brand pack is
written once and dropped into any of them; keys a given application has no use for are ignored rather
than refused. `wwwroot/themes/graphite.yaml` is the built-in look written out as a pack, and doubles as
the worked example. A pack may also carry `fonts`, `layout` (corner radii and `pageWidth`, the widest the
content grows, default `1440px`), `branding`, and a `customCSS` block for the rare rule a token cannot express.

`branding` sets the top bar and the browser tab: `titlePlain` (the name, default "FiGet"), `logo`, `favicon`
(default the logo, else FiGet's own mark), `logoAlt` (default the name) and `hideTitle` (drop the name when the
logo carries it). `logo` and `favicon` are each a file next to the pack - SVG, PNG, WebP, JPEG or GIF,
at most 1 MB, served by FiGet at `/themes/{pack}/assets/{file}` with a policy that runs nothing - or a
`data:image/...` URL. A link to another site is not accepted: the logo would depend on that site and every reader's
browser would call it. The top bar is dark in both light and dark mode, so one logo serves both; draw it for a dark
background. An organisation's logo belongs in its own pack on its own deployment (`Path` pointed at a mounted
volume), not in this repository. Both shipped packs carry FiGet's own mark as the example.

| Key | Default | Meaning |
| --- | --- | --- |
| `Theme` | empty | Name of the pack to serve. Empty uses the built-in look. |
| `Path` | `themes` under the web root | Where packs are read from. Point it at a mounted volume to change themes without rebuilding the image. Packs are read at start; the appearance page's *Reload packs from disk* re-reads them on the instance that served the click, and other replicas read them when they next start. |

## FiGet:DataProtection

The data-protection key ring protects the sign-in cookies, the antiforgery tokens and the stored client secrets of
sign-in providers. It is kept in the database, so every replica shares it.

| Key | Default | Meaning |
|---|---|---|
| `MasterKey` | empty | 32 random bytes in base64 (`openssl rand -base64 32`) that encrypt the key ring. From a secret (on OpenShift an ESO-synced Secret as `FiGet__DataProtection__MasterKey`), never a committed file. Empty: the ring is stored as plain XML with a warning at every start, and anyone who can read the database can forge a sign-in cookie. Required when `Database:ExpectedReplicas` is above 1. |

Setting a master key encrypts the keys already stored at the next start, in place: sessions and provider secrets stay
valid. Keep the key: without it, or with another one, the ring cannot be read, every session ends, stored provider
secrets have to be entered again, and a new key is made (in plain text if no master key is set).

## FiGet:Auth

| Key | Default | Meaning |
|---|---|---|
| `BootstrapAdminToken` | empty | A secret registered on start as a service token with admin scope, for automation that must work before anyone has signed in. It does not sign in to the web UI: people use accounts. On clusters, set it from a secret so all replicas agree. |
| `Recovery:UserName` | empty | With `Recovery:Password`: on start, this account is created if missing, enabled, unlocked, made super admin, given that password, and required to choose a new one at sign-in. For when nobody can sign in any more. The log warns on every start while it is set; remove it afterwards. |
| `Recovery:Password` | empty | The one-time password for `Recovery:UserName`. From a secret, never a committed file. |

When no account exists, FiGet creates the first administrator on start: user name `admin`, password `admin`, role
super admin, and a new password required at the first sign-in before any page works. Sign in and replace it straight
away on an instance anyone else can reach. Five wrong passwords lock an account for fifteen minutes. Accounts,
roles and what is still to come are described in `docs/auth-plan.md`.

### Sign-in providers

OpenID Connect providers are not configuration: a super admin adds them under **Admin > Authentication > Providers**,
and they are stored in the database, so a change applies at the next sign-in on every replica without a restart. The
client secret is encrypted with the data-protection keys, which are also in the database; an instance that loses those
keys shows the secret as unreadable and it has to be entered again.

At the provider, register a confidential client using the authorization code flow, with the redirect URI the provider
page shows: `{public base URL}/signin-oidc/{slug}`. Behind a reverse proxy set `FiGet:PublicBaseUrl`: the redirect
URI sent to the provider is built from it. The provider's issuer must be HTTPS unless it runs on the same machine.
Scopes default to `openid profile email`; for group mapping, name the claim that lists groups (`groups` for
Authentik and Keycloak) and link provider groups on each FiGet group's page.

`/account/login/local` always shows the user name and password form, whatever the sign-in page shows.

## FiGet:RateLimits

Per client address, for what can be done without proving who you are. Requests with a valid API key, and signed-in
browsers, are not limited (sign-in attempts are, signed in or not).

| Key | Default | Meaning |
|---|---|---|
| `AnonymousRequestsPerMinute` | `1200` | Protocol reads on anonymous-read feeds, asset downloads and pages, without a valid key or sign-in, and requests that send a key or password that is refused. A request that sends no credential at all to a feed that needs one is not counted: its answer is the 401 challenge, which NuGet clients with stored credentials ask for before every request. `0` turns it off. |
| `AnonymousBurst` | `600` | How many of those may arrive at once before the rate applies. An install of a meta-module is a burst. |
| `SignInAttemptsPerMinute` | `20` | Sign-in posts, local and provider buttons. The next one goes back to the sign-in page with a message. `0` turns it off. |

A refused protocol request gets `429` with `Retry-After`. The address is the connection's, as the forwarded-headers
middleware resolves it behind a trusted proxy (`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, or known proxies configured);
the `X-Forwarded-For` header is never read directly. A feed's *Allowed networks* (its settings page) use the same address,
so behind a proxy without that switch every client is the proxy's own address and a list would admit all or none. **Behind a CDN or a shared egress** many clients arrive from few
addresses: raise the limits, or configure the forwarded headers so the real client address is used. A garbage key does
not get around the limit: a request is counted as anonymous after its key has been checked.

## FiGet:Limits

| Key | Default | Meaning |
|---|---|---|
| `MaxPackageSizeMB` | `256` | Largest accepted package or symbol package upload. Larger uploads get 413. |
| `MaxImportSizeMB` | `4096` | Largest archive an asset import accepts, and the most it may unpack to, counted on bytes actually unpacked. Past it the import stops with 413. |
| `MaxAssetSizeMB` | `1024` | Largest file an asset directory accepts. Separate from the package limit because installers are far larger. Larger uploads get 413 and leave nothing behind. A reverse proxy in front has its own body limit, which has to be at least this. |

## FiGet:Assets

| Key | Default | Meaning |
|---|---|---|
| `IncompleteUploadExpiry` | `24:00:00` | How long the parts of a multipart upload wait for completion before the hourly sweep removes them. |
| `SharesRoot` | empty | A folder on the server whose direct sub-folders - the shares - an administrator may choose on the pages as the content of an asset directory, the whole share or a folder inside it: the create form under *Content*, and *Content* on the directory's settings page (admins only). Mount each share as a sub-folder of it (`/shares/intune` under `/shares`). The pages offer share names and walk a folder inside one real directory at a time, never taking a path, so nothing outside this folder can be served. Only real folders count: no links or junctions, nothing hidden or system, none of the never-served names, at most eight levels deep. Empty: the pages offer no folders, and a folder-backed directory comes only from `Feeds:N:Folder`. |
| `RemoteFetch:Timeout` | `00:30:00` | How long fetching one file by URL may take, download included. |
| `RemoteFetch:AllowPrivateNetworks` | `false` | Whether a fetched URL may point at a private network, loopback or carrier-grade NAT address. Off, because an upload token could otherwise read internal addresses through the server. Link-local (cloud metadata) addresses stay refused either way. Applies without a proxy. |
| `RemoteFetch:Proxy` | empty | An HTTP proxy to fetch through (`http://proxy.example:8080`; credentials in the URL, from a secret, when it needs them). Requires `AllowedHosts`: through a proxy the server cannot see the address it reaches, so the host name is what is checked. |
| `RemoteFetch:AllowedHosts` | empty | Hosts a fetch may go to, every redirect hop included: an exact name or `*.example.com` for names below it. Empty allows any host, which only works without a proxy. With it set and no proxy, both the host and the connected address are checked. |

## FiGet:Logging

| Key | Default | Meaning |
|---|---|---|
| `Json` | `false` | Write logs as JSON to the console. Always on when `DOTNET_RUNNING_IN_CONTAINER=true` (set in the image). |
| `Requests` | `false` | One line per request: method, path, status, duration, caller address, forwarded address, token or user, user agent. Off by default; turn it on to see whether a client reached this server and what it asked for. Skipped: health probes, the framework's own paths, and browser assets — stylesheets, scripts, icons and fonts, which a browser re-fetches on every page. Anything under `/nuget` or `/endpoints` is always kept, whatever it is named, so a package called `something.css` or an installer called `setup.js` is never filtered away. The query string is logged except on the sign-in callback, where it would carry a provider's authorization code. |

### The audit log

Who changed what, and when: feeds created, edited and deleted, upstreams added and removed, tokens and personal
keys issued and revoked, the theme changed, packages pushed, deleted, relisted, pulled and un-cached, asset files
uploaded (multipart included), fetched by URL and deleted, archives imported, asset folders created, asset metadata
changed; accounts, groups, permissions and sign-in providers changed; sign-ins, local and through a provider,
including refused and locked ones; and keys that no longer work but are still being sent (`token.refused`, at most
once per ten minutes per key, address and feed, never with anything of the secret).

It has no on/off key of its own. Every line is written at Information under the category `FiGet.Audit`, so
the standard log-level configuration governs it:

| Setting | Effect |
|---|---|
| (nothing) | On, because `Logging:LogLevel:Default` is `Information`. |
| `Logging:LogLevel:FiGet.Audit` = `None` | Off. |

On by default is deliberate. An audit trail that has to be switched on in advance is not there on the day
somebody asks what happened, and the volume is a few lines a day rather than a few per request — unlike
`Requests` above, which is off by default for exactly that reason.

The same entries are stored in the database and shown on **Admin > Manage > Audit log**, filterable by event,
account or token, feed and date. A request never waits for that: entries are queued and written in batches by a
background writer, so the console line is the one that is certain, and the writer logs an error if it ever has to
give entries up (queue full, database down for three attempts). Silencing the category silences the console line only;
the row is still stored.

| Key | Default | Meaning |
|---|---|---|
| `FiGet:Audit:RetentionDays` | `365` | Entries older than this are deleted. `0` keeps them forever. How often the check runs is `FiGet:Jobs:AuditPrune`; it takes a lease in the database first, so with several replicas one of them prunes and the others skip it. |

## FiGet:Changes

A report per feed of what it gained and what its upstreams now offer for the packages it holds - the answer to "has
anything we depend on moved?". Every feed shows it on its own **What's new** page and answers
`GET /api/packages/{feed}/changes?days=N`; these settings are only about posting it somewhere.

The address is a **secret**: a Teams, Slack or Power Automate webhook carries its token in its path. It can be set here
from the environment, or on **Admin > Change reports**, where it is stored encrypted with the data-protection key ring
and shown back as a host only. A stored address wins over this one, and a feed's own address (set the same way) wins
over both - which is how one webhook per channel is done. Without `FiGet:DataProtection:MasterKey` that key ring sits
unencrypted in the database, so set one.

| Key | Default | Meaning |
|---|---|---|
| `FiGet:Changes:Webhook:Url` | empty | Where to post. Empty, and with nothing stored on the pages, nothing is posted. **A secret**: set it from the environment (`FiGet__Changes__Webhook__Url`). |
| `FiGet:Changes:Webhook:Format` | `Json` | `Json` for an automation runner or a relay; `Chat` writes one summary into both `content` and `text`, which a plain Discord or Slack webhook renders as it is; `Teams` posts an adaptive card, for a flow that forwards the body to Teams as one. |
| `FiGet:Changes:Webhook:Feeds` | empty | Feed names to report on. Empty: every package feed. |
| `FiGet:Changes:Webhook:MaxDays` | `7` | The longest window one report may cover, so a server that was off for a month posts a week of change rather than a wall. |
| `FiGet:Changes:Webhook:SendWhenEmpty` | `false` | Whether a report with nothing in it is posted. Off: silence means nothing moved. |
| `FiGet:Changes:Webhook:HeaderName` | empty | One extra request header, for a relay that authenticates with a bearer token rather than a token in the URL. |
| `FiGet:Changes:Webhook:HeaderValue` | empty | Its value. **A secret**; from the environment. |
| `FiGet:Changes:Webhook:Timeout` | `00:00:30` | How long one post may take. |
| `FiGet:Changes:Webhook:AllowPrivateNetworks` | `false` | Whether the receiver may be on a private or loopback address, as an internal relay is. The link-local block stays refused either way. |
| `FiGet:Changes:Webhook:MaxNotes` | `25` | How many versions one run may fetch release notes for. Only versions an upstream offers and nobody here has fetched need it; what a feed holds carries its own. `0` switches the fetching off. |

Two things this deliberately does not do. It follows no redirect - a 3xx would re-send the whole body to an address
nobody vetted - and it never writes the address anywhere: a page, a log line and an audit entry all say the host and
no more. How often it runs is `FiGet:Jobs:ChangeReport`; a feed's routing label is on the feed's own settings page.

## FiGet:Jobs

How often each background job runs. Every default is what that job did before these settings existed, so an instance
that sets none of them behaves exactly as it did. A value of `0` (or `00:00:00`) switches a job off: its background
service is then not registered at all, so nothing ticks and nothing takes a lease. That is for an operator who wants
the work done elsewhere - one replica of many, a maintenance window - not a way to make a job cheaper.

The interval is also how long the runner holds that job's lease, so a longer interval widens the window in which one
replica owns it.

| Key | Default | Meaning |
|---|---|---|
| `FiGet:Jobs:Retention` | `01:00:00` | Retention and cache pruning, feed by feed. |
| `FiGet:Jobs:AuditPrune` | `06:00:00` | Deleting audit entries past `FiGet:Audit:RetentionDays`. |
| `FiGet:Jobs:UploadSweep` | `01:00:00` | Removing multipart uploads nobody finished (`FiGet:Assets:IncompleteUploadExpiry`). |
| `FiGet:Jobs:UsagePrune` | `1.00:00:00` | Deleting usage counts past ninety days. |
| `FiGet:Jobs:CatalogueSweep` | `1.00:00:00` | Refreshing the stored upstream catalogue of every id a proxy feed holds, so a package nobody browsed is still known to have moved. This interval is also what counts as fresh: a catalogue fetched more recently than this is left alone, so on a feed people actually browse a run queues nothing and records nothing. Silence from this job means there was nothing to do. |
| `FiGet:Jobs:ChangeReport` | `1.00:00:00` | Posting each feed's change report, and keeping release notes current for the feeds' own pages. |

Two schedules are deliberately not settings. Usage counts are flushed from memory to the database every minute: that
is the write path rather than a schedule, and switching it off would lose counts instead of deferring them. And every
job waits five minutes after start-up before its first run, so a restart loop cannot become a removal loop.


## What survives a restart

The version list an upstream reported has always been in the database. Two facts *about* those versions
are now stored beside it, because both change what a client is told:

| Stored | Why |
|---|---|
| Which versions the upstream does not advertise | Without it every hidden version looks listed until the first refresh lands, and can win "latest" again. |
| What each version depends on | A client reads this to decide what else to install. While it was missing, a first install of an uncached module brought none of its dependencies. |

Descriptions, summaries and tags are stored too, since 2026-09-13, per version with each distinct tag list kept
once, and written in batches with their own database context. The first attempt kept them on tracked entities and
caused an out-of-memory incident while this was being built; the current shape does not. A restart reads them
back, so listings stay described.

## Standard ASP.NET Core and OpenTelemetry settings that matter

| Setting | Meaning |
|---|---|
| `ASPNETCORE_HTTP_PORTS` | Listening port; `8080` in the image. |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true` behind a reverse proxy. Honours `X-Forwarded-For` (the address rate limits and the audit log use) and `X-Forwarded-Proto`; not `X-Forwarded-Host`, so set `FiGet:PublicBaseUrl` as well. It trusts whoever connects, so only the proxy may be able to reach the container's port: a client that reaches it directly chooses its own address. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | When set, traces and metrics are exported over OTLP. |
| `OTEL_SERVICE_NAME` | Overrides the service name `figet`. |

## Health endpoints

| Path | Meaning |
|---|---|
| `/health/live` | The process is up. No dependencies checked. |
| `/health/ready` | The database is reachable. |

## Client credentials

Clients present a token in any of these ways; the first valid one that grants the operation wins:

- `X-NuGet-ApiKey: <token>` (what `dotnet nuget push`, nuget.exe and PSResourceGet `-ApiKey` send),
- `X-ApiKey: <token>`,
- `Authorization: Basic base64(anything:<token>)` (the password of a NuGet source credential),
- `Authorization: Bearer <token>`.

A feed's v3 service index (`v3/index.json`) answers without credentials, so a client that sends its API key only with the push can find where to push; it lists the feed's endpoint addresses and nothing it holds. Any other read request to a feed without anonymous read and without valid credentials gets 401 with
`WWW-Authenticate: Basic realm="FiGet"`, so NuGet clients retry with configured credentials. A valid token
lacking the scope gets 403. Push, Delete and Admin scopes include Read.

## Developer database migrations

```
dotnet tool restore
dotnet ef migrations add <Name> --project src/FiGet.Infrastructure.Sqlite --output-dir Migrations
dotnet ef migrations add <Name> --project src/FiGet.Infrastructure.SqlServer --output-dir Migrations
```

Always add a migration to both. CI fails when either provider's model snapshot drifts from the model.

## Running the SQL Server tests

```
FIGET_TEST_SQLSERVER="Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True" dotnet test
```

The value is a connection string without a database; each test class creates and drops its own database.
