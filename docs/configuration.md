# Configuration

Every setting FiGet reads. A key that is not in this file does not exist. Settings come from
`appsettings.json`, environment variables (`FiGet__Database__Provider`), or any other ASP.NET Core
configuration source. Never put secrets in files that are committed; use environment variables or mounted
secret files.

## FiGet

| Key | Default | Meaning |
|---|---|---|
| `FiGet:PublicBaseUrl` | empty | Absolute base URL used in every URL the protocols emit, for example `https://packages.example.org`. Empty: derived from the request. Behind a reverse proxy either set this, or set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` so `X-Forwarded-Proto` and `X-Forwarded-Host` are honoured. |

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
| `Feeds:N:Kind` | `Curated` | `Curated` or `Proxy` (proxy behaviour arrives in phase 3). |
| `Feeds:N:AnonymousRead` | `false` | When true, every read endpoint works without credentials. |
| `Feeds:N:AllowOverwrite` | `false` | When true, pushing an existing version replaces it instead of answering 409. |
| `Feeds:N:DeletionBehavior` | `Unlist` | `Unlist` hides the version from search and keeps it downloadable; `HardDelete` removes the metadata and the files. |
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
| `UpstreamIndexTtl` | `00:05:00` | How long one upstream's version list for one package stays usable before it is fetched again. A new upstream release becomes visible within this window. |
| `UpstreamTimeout` | `00:00:10` | How long a single upstream call may take before that upstream counts as unavailable for this request. |

## FiGet:Theming

A theme pack is a JSON file of custom-property overrides, compiled once and served at
`/themes/{name}.css` after `app.css`. Every token has a default in the base stylesheet, so a pack that
sets three colours is a complete theme.

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

## FiGet:Logging

| Key | Default | Meaning |
|---|---|---|
| `Json` | `false` | Write logs as JSON to the console. Always on when `DOTNET_RUNNING_IN_CONTAINER=true` (set in the image). |

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
dotnet ef migrations add <Name> --project src/FiGet.Persistence.Sqlite --output-dir Migrations
dotnet ef migrations add <Name> --project src/FiGet.Persistence.SqlServer --output-dir Migrations
```

Always add a migration to both. CI fails when either provider's model snapshot drifts from the model.

## Running the SQL Server tests

```
FIGET_TEST_SQLSERVER="Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True" dotnet test
```

The value is a connection string without a database; each test class creates and drops its own database.
