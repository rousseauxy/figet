# Configuration

Every setting FiGet reads. A key that is not in this file does not exist. Settings come from
`appsettings.json`, environment variables (`FiGet__Database__Provider`), or any other ASP.NET Core
configuration source. Never put secrets in files that are committed; use environment variables or mounted
secret files.

## FiGet

| Key | Default | Meaning |
|---|---|---|
| `FiGet:PublicBaseUrl` | empty | Absolute base URL used in every URL the protocols emit, for example `https://packages.example.org`. Empty: derived from the request. Behind a reverse proxy either set this, or set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` so `X-Forwarded-Proto` and `X-Forwarded-Host` are honoured. |
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
| `TempPath` | system temp directory | Where uploads are buffered while they are validated. Must have room for the largest package. |

## FiGet:Feeds

A list of feeds created on start when they do not exist. Existing feeds are never changed from
configuration. If no feed exists after seeding, a feed named `default` is created.

| Key | Default | Meaning |
|---|---|---|
| `Feeds:N:Name` | required | Letters, digits, `.`, `-`, `_`; 1 to 64 characters; starts with a letter or digit. Case-insensitive in URLs. |
| `Feeds:N:Kind` | `Curated` | `Curated`, `Proxy` or `Assets`. A feed with upstreams is a proxy feed whatever this says. `Assets` makes an asset directory: files by path under `/endpoints/{name}`, described in `docs/protocol-assets.md`; `AllowOverwrite`, `DeletionBehavior` and upstreams do not apply to it. |
| `Feeds:N:AnonymousRead` | `false` | When true, every read endpoint works without credentials. |
| `Feeds:N:AllowOverwrite` | `false` | When true, pushing an existing version replaces it instead of answering 409. |
| `Feeds:N:DeletionBehavior` | `Unlist` | `Unlist` hides the version from search and keeps it downloadable; `HardDelete` removes the metadata and the files. |
| `Feeds:N:MergePushedIdsWithUpstreams` | `false` | When false, an id with a version pushed to the feed is served only from the feed: its upstreams are not asked about it, and copies of it cached from an upstream are unlisted. When true, the pushed and upstream versions are merged into one list, so the higher version of either is latest. Only right when the upstream package really is the same package. |
| `Feeds:N:Upstreams:M:Name` | required with an upstream | A name for logs and the UI, unique within the feed. |
| `Feeds:N:Upstreams:M:Url` | required with an upstream | A v3 service index (`https://api.nuget.org/v3/index.json`) or a v2 feed root (`https://www.powershellgallery.com/api/v2`). |
| `Feeds:N:Upstreams:M:Kind` | `V3` | `V3` or `V2`. The URL alone cannot always tell, so it is stated. |
| `Feeds:N:Upstreams:M:Allow:X` | empty | Regular expressions on the package id. Empty allows every id; otherwise an id must match one to be listed or fetched. |
| `Feeds:N:Upstreams:M:Deny:X` | empty | Regular expressions on the package id. A match is never listed or fetched, even when it is allowed above. |
| `Feeds:N:Upstreams:M:CredentialRef` | empty | Name of the environment variable holding this upstream's API key or password. The secret itself is never stored. |

Environment variable form: `FiGet__Feeds__0__Name=modules`, `FiGet__Feeds__0__AnonymousRead=true`.

## FiGet:Connector

Applies to every proxy feed. Upstreams themselves are configured per feed, above.

| Key | Default | Meaning |
| --- | --- | --- |
| `UpstreamIndexTtl` | `00:05:00` | How old one upstream's cached catalogue for a package may get before it is fetched again. Not an expiry: a catalogue older than this is still served immediately and refreshed behind the request, so only the first ever view of a package waits for the upstream. A new upstream release becomes visible to the reader after this window, on the view that follows the refresh. |
| `UpstreamTimeout` | `00:00:30` | How long one upstream call may take before that upstream counts as unavailable for this request. Listing a package with hundreds of versions on a v2 gallery is a paged walk of several megabytes, so this is not the latency of one request. A timeout is treated as "the upstream did not answer": the last known list is served and nothing is considered withdrawn. |
| `MaxDescribedPackages` | `500` | Package ids one replica keeps upstream descriptions in memory for before dropping the oldest. The descriptions are the large part and every replica holds its own copy, so this decides the memory a busy instance settles at. Lowering it costs listings their description text until the next refresh, never their correctness. |

## FiGet:Theming

A theme pack is a YAML file of custom-property overrides, compiled once and served at
`/themes/{name}.css` after `app.css`. Every token has a default in the base stylesheet, so a pack that
sets three colours is a complete theme.

The token names and the file format are shared with the sibling applications here, so a brand pack is
written once and dropped into any of them; keys a given application has no use for are ignored rather
than refused. `wwwroot/themes/graphite.yaml` is the built-in look written out as a pack, and doubles as
the worked example. A pack may also carry `fonts`, `layout` (corner radii) and a `customCSS` block for
the rare rule a token cannot express.

| Key | Default | Meaning |
| --- | --- | --- |
| `Theme` | empty | Name of the pack to serve. Empty uses the built-in look. |
| `Path` | `themes` under the web root | Where packs are read from. Point it at a mounted volume to change themes without rebuilding the image. |

## FiGet:Auth

| Key | Default | Meaning |
|---|---|---|
| `BootstrapAdminToken` | empty | A secret registered as an admin token on start. When empty and no active admin token exists, FiGet generates one and writes it to the log once. On clusters, set it from a secret so all replicas agree. |

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
| `RemoteFetch:Timeout` | `00:30:00` | How long fetching one file by URL may take, download included. |
| `RemoteFetch:AllowPrivateNetworks` | `false` | Whether a fetched URL may point at a private network, loopback or carrier-grade NAT address. Off, because an upload token could otherwise read internal addresses through the server. Link-local (cloud metadata) addresses stay refused either way. No HTTP proxy is used for fetching. |

## FiGet:Logging

| Key | Default | Meaning |
|---|---|---|
| `Json` | `false` | Write logs as JSON to the console. Always on when `DOTNET_RUNNING_IN_CONTAINER=true` (set in the image). |
| `Requests` | `false` | One line per request: method, path, status, duration, caller address, forwarded address, token or user, user agent. Off by default; turn it on to see whether a client reached this server and what it asked for. Skipped: health probes, the framework's own paths, and browser assets — stylesheets, scripts, icons and fonts, which a browser re-fetches on every page. Anything under `/nuget` is always kept, whatever it is named. |

### The audit log

Who changed what, and when: feeds created, edited and deleted, upstreams added and removed, tokens issued
and revoked, the theme changed, packages pushed, deleted, relisted, pulled and un-cached, asset files uploaded
(multipart included), fetched by URL and deleted, archives imported, asset folders created, asset metadata changed, and sign-ins including refused ones.

It has no on/off key of its own. Every line is written at Information under the category `FiGet.Audit`, so
the standard log-level configuration governs it:

| Setting | Effect |
|---|---|
| (nothing) | On, because `Logging:LogLevel:Default` is `Information`. |
| `Logging:LogLevel:FiGet.Audit` = `None` | Off. |

On by default is deliberate. An audit trail that has to be switched on in advance is not there on the day
somebody asks what happened, and the volume is a few lines a day rather than a few per request — unlike
`Requests` above, which is off by default for exactly that reason.

Console only for now. A container log rotates and is lost; the database table and admin page this wants
eventually are described in `docs/backlog.md`.

## What survives a restart

The version list an upstream reported has always been in the database. Two facts *about* those versions
are now stored beside it, because both change what a client is told:

| Stored | Why |
|---|---|
| Which versions the upstream does not advertise | Without it every hidden version looks listed until the first refresh lands, and can win "latest" again. |
| What each version depends on | A client reads this to decide what else to install. While it was missing, a first install of an uncached module brought none of its dependencies. |

Descriptions, summaries and tags are deliberately **not** stored. They are two orders of magnitude larger —
a hundred megabytes against tens of kilobytes for the facts above — and persisting them is what caused the
out-of-memory incident recorded in `docs/status.md`. They live in memory, and a restart simply leaves
listings undescribed until the first refresh fills them in.

## Standard ASP.NET Core and OpenTelemetry settings that matter

| Setting | Meaning |
|---|---|
| `ASPNETCORE_HTTP_PORTS` | Listening port; `8080` in the image. |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true` behind a reverse proxy that sets `X-Forwarded-*`. |
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

A read request to a feed without anonymous read and without valid credentials gets 401 with
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
