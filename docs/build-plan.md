# FiGet build plan

Handover document. A fresh agent or developer should be able to start phase 1 from this file
alone. It states what to build, in what order, how each step is verified, and the traps already
known. Where this document and the code disagree, the code is wrong until a decision here is
changed on purpose.

Status: written 2026-09-11 after the feasibility study. Phase 1 done the same day; corrections found while
building it are folded in below; what was verified is in `docs/status.md`.

---

## 1. Goals and non-goals

### Goals

1. A NuGet-compatible server that works with **every PowerShell client in real use**: Windows
   PowerShell 5.1 with PowerShellGet 2.x (NuGet v2 OData), PowerShell 7 with PSResourceGet (v2
   or v3, auto-detected from the URI), and the .NET tooling (nuget.exe, dotnet CLI, v3).
2. **Proxy feeds** with local caching in front of nuget.org (v3) and the PowerShell Gallery
   (v2), with one merged version list per package (§5).
3. **Curated feeds** (push-only) with retention rules.
4. **Asset directories**: path-addressed file storage served by plain `GET`.
5. **OpenID Connect** against any provider, several at once, plus API keys and personal access
   tokens for non-interactive clients.
6. One container image that runs **stand-alone with SQLite** or on **Kubernetes / OpenShift
   with SQL Server** and shared storage, unchanged.
7. **Drop-in URL compatibility** with the commercial server most fleets are leaving (§4.1), so
   an existing fleet switches by changing DNS.

### Feed-type priority

Decided 2026-09-11, highest first. When time or scope has to give, a lower type never delays a
higher one.

1. **Assets**: directories of files served by plain `GET`.
2. **PowerShell**: NuGet v2 and v3 for every PowerShell client, with a PowerShell Gallery proxy.
3. **.NET**: NuGet v3 for nuget.exe and the dotnet CLI, with a nuget.org proxy and authenticated
   upstreams (for example GitHub Packages with a service token), so build machines without
   internet access or personal tokens can restore. Most of it falls out of the v3 surface and the
   proxy phase; it stays on the list as long as it stays cheap.

Later, on demand: Chocolatey, then npm and PyPI, which are separate protocols.

### Non-goals (for now)

- Docker/OCI registry and Maven.
- npm and PyPI while the three feed types above are unfinished. Chocolatey is NuGet v2 and may
  come almost free after phase 2.
- WebDAV on asset directories.
- A catalog resource (v3 `Catalog/3.0.0`) or repository signing.
- High availability of the database itself. That is the database's job.

---

## 2. Technology choices (decided)

| Concern | Choice | Why |
|---|---|---|
| Runtime | .NET 10, ASP.NET Core minimal APIs, C# with nullable + implicit usings | Current LTS-track SDK on the dev machine (`global.json`) |
| NuGet plumbing | `NuGet.Protocol`, `NuGet.Packaging`, `NuGet.Versioning`, `NuGet.Frameworks` at the version that ships with the SDK (`dotnet nuget --version`) | Upstream clients for v2 and v3, nuspec reader, version normalisation, dependency ranges. Never re-implement these. |
| Persistence | EF Core 10; **SQL Server primary, SQLite secondary**; two migration assemblies | Production runs SQL Server; stand-alone and tests run SQLite. No provider-specific SQL. |
| Storage | `IPackageStorage`, `IAssetStorage` abstractions; local filesystem first, S3-compatible second | Local disk for dev and NAS; object storage or RWX volume for multi-replica clusters |
| Web UI | Blazor with **static server rendering** (form posts, no interactive circuits), minimal, no component library | Same stack, no second build system; no sticky sessions needed across replicas |
| Auth (humans) | ASP.NET Core `OpenIdConnect` handler, N named schemes from configuration, cookie session | Provider-agnostic. **Not** `Microsoft.Identity.Web` (Entra-only). |
| Auth (clients) | API keys and PATs, hashed at rest, accepted as `X-NuGet-ApiKey`, `X-ApiKey`, `Authorization: Basic` (password = token) | What nuget.exe, PowerShellGet, PSResourceGet and existing scripts send |
| Logging / metrics | `Microsoft.Extensions.Logging` to stdout (JSON in containers), OpenTelemetry metrics + traces, OTLP exporter | Cluster-native |
| Container | `mcr.microsoft.com/dotnet/aspnet:10.0` base, non-root, port 8080, data under `/data` | Passes OpenShift restricted SCC unchanged |
| Licence | MIT | Decided |

---

## 3. Solution layout

```
figet.slnx
src/
  FiGet.Domain/          Entities and pure rules. One package reference, NuGet.Versioning, because
                         version comparison is the domain and not a detail. No EF, no ASP.NET, no
                         NuGet.Protocol or NuGet.Packaging.
                         Entities: Feed, FeedUpstream, Package, PackageVersion, PackageDependency,
                                   AssetDirectory, Asset, User, Role, ApiKey, AuditEntry, CachedUpstreamIndex
                         Rules:    VersionListBuilder (§5), SearchQueryParser, FeedNames
  FiGet.Application/     What the server does, against ports. References Domain only.
                         Ports/      IPackageStore, IFeedStore, IAccessTokenStore, IPackageStorage,
                                     IUpstreamClient, IUpstreamIndexStore, IPackageIndexer
                         Services:   ConnectorService (§5), PackageIngestionService, AccessTokenService
  FiGet.Infrastructure/  The adapters behind those ports:
                         Persistence/  EF Core DbContext + configurations (provider-neutral) + EF stores
                         Storage/      IPackageStorage on the file system
                         Upstream/     IUpstreamClient over NuGet.Protocol (v2 and v3 upstreams)
                         Packages/     IPackageIndexer over NuGet.Packaging
  FiGet.Infrastructure.SqlServer/   migrations for SQL Server
  FiGet.Infrastructure.Sqlite/      migrations for SQLite
  FiGet.Storage.S3/      S3-compatible implementation (phase 6)
  FiGet.Http/            shared ASP.NET Core helpers: feed resolution and access checks, credential
                         extraction, public URLs, upload buffering (used by every protocol project)
  FiGet.Protocol.V3/     endpoints + JSON models for NuGet v3 (§4.2)
  FiGet.Protocol.V2/     endpoints + OData Atom writer + $filter parser for NuGet v2 (§4.3)
  FiGet.Management/      /api/packages/... compatibility API (§4.5) + FiGet's own admin API
  FiGet.Assets/          /endpoints/... asset directory endpoints (§4.4)
  FiGet.Web/             host: DI, config, auth, Blazor admin UI, health, OpenTelemetry
tests/
  FiGet.Testing/             shared test helpers (builds real .nupkg/.snupkg files in memory)
  FiGet.Core.Tests/          unit tests (version list merge, filter parser, indexer)
  FiGet.Protocol.Tests/      golden fixture tests: request in, exact body out (§7.1)
  FiGet.Integration.Tests/   WebApplicationFactory against SQLite and (when available) SQL Server
  FiGet.Compat/              PowerShell scripts driving real clients against a running instance (§7.2)
  fixtures/                  recorded + scrubbed request/response pairs, synthetic .nupkg files
docs/
  build-plan.md              this file
  protocol-v2.md             grows from phase 0 recordings
  protocol-v3.md             grows from phase 1
  configuration.md           every config key, added as implemented
deploy/
  Dockerfile
  compose.example.yml
  helm/                      phase 6
```

Project references flow inward: `Web -> Protocol.* / Management / Assets -> Core`, `Web ->
Persistence.* / Storage.*`. Protocol projects depend on Core interfaces only, never on EF.

---

## 4. Protocol surface

All feed URLs hang off `/nuget/{feed}`. The same feed is reachable through three roots so that
every client auto-detects correctly:

| Root | Who uses it | Detection rule in the client |
|---|---|---|
| `/nuget/{feed}/` | PowerShellGet 2.x, nuget.exe as a v2 source | Root answers with the OData service document |
| `/nuget/{feed}/api/v2` | PSResourceGet in v2 mode, anything that expects the nuget.org legacy shape | PSResourceGet: URI ends with `/api/v2` |
| `/nuget/{feed}/v3/index.json` | dotnet, nuget.exe, PSResourceGet in v3 mode, PackageManagement's NuGet provider 3.0.0.1 (bundled with PackageManagement 1.4.8.1) | URI ends with `/v3/index.json`; the provider reads the service index `version` |

`/nuget/{feed}/` and `/nuget/{feed}/api/v2` are the **same** v2 root; implement once, route
twice. ~~Also serve the PSResourceGet "NuGet.Server" shape by making `/nuget/{feed}/nuget` an
alias.~~ Dropped 2026-09-15: PSResourceGet's NuGet.Server mode cannot find exact versions
(PSResourceGet #1206, #1896), so the alias would lead clients into a broken mode. A feed named
`nuget` is warned about on its settings page, and `Packages(Id,Version)/Download` redirects to the
package download for a client already in that mode.

### 4.1 URL compatibility contract

These paths are frozen. They are what existing fleets, scripts and Ansible tasks already call.

```
GET/PUT      /nuget/{feed}/                                  v2 root; PUT = push (nuget.exe pushes to the source URL itself)
GET          /nuget/{feed}/$metadata
GET          /nuget/{feed}/Packages()  …/Packages(Id='x',Version='y')  …/Packages()/$count
GET          /nuget/{feed}/FindPackagesById()?id='x'  …/FindPackagesById()/$count
GET          /nuget/{feed}/Search()?searchTerm='…'&…  …/Search()/$count
GET          /nuget/{feed}/GetUpdates()?packageIds='a|b'&versions='1|2'&…
GET          /nuget/{feed}/package/{id}/{version}            nupkg download (v2)
PUT          /nuget/{feed}/api/v2/package   and   /nuget/{feed}/package     push (multipart "package")
DELETE       /nuget/{feed}/api/v2/package/{id}/{version}     unlist (or hard delete per feed setting)
             /nuget/{feed}/api/v2/…                          every GET above, same handlers
GET          /nuget/{feed}/v3/index.json                     service index
GET          /nuget/{feed}/v3/registration/{id}/index.json   …/{id}/{version}.json
GET          /nuget/{feed}/v3/flatcontainer/{id}/index.json  …/{id}/{version}/{id}.{version}.nupkg  …/{id}.nuspec
GET          /nuget/{feed}/v3/query?q=&skip=&take=&prerelease=&semVerLevel=
GET          /nuget/{feed}/v3/autocomplete?q=&id=&skip=&take=&prerelease=
PUT/DELETE   /nuget/{feed}/v3/publish  …/publish/{id}/{version}     PackagePublish/2.0.0
PUT          /nuget/{feed}/v3/symbolpublish                  SymbolPackagePublish/4.9.0
GET          /nuget/{feed}/symbols/{file}/{key}/{file}       symbol server
GET/PUT/DELETE/HEAD  /endpoints/{directory}/content/{path}   asset content
GET          /endpoints/{directory}/dir/{path}               asset folder listing (JSON)
GET/POST     /endpoints/{directory}/metadata/{path}          asset metadata
GET          /api/packages/{feed}/versions?name=&version=    compatibility API (§4.5)
GET          /api/packages/{feed}/latest?name=
POST         /api/packages/{feed}/delete?name=&version=
```

Feed names are case-insensitive. Package ids are case-insensitive everywhere and stored with
the case of first publication; v3 URLs use the lower-cased id as nuget.org does.

### 4.2 NuGet v3

Reference: the NuGet server API documentation on learn.microsoft.com ("NuGet Server API") and
nuget.org's own responses. Shapes below are the minimum; add fields, never remove.

**Service index** `v3/index.json`

```json
{
  "version": "3.0.0",
  "resources": [
    { "@id": "…/v3/query",            "@type": "SearchQueryService" },
    { "@id": "…/v3/query",            "@type": "SearchQueryService/3.0.0-beta" },
    { "@id": "…/v3/query",            "@type": "SearchQueryService/3.5.0" },
    { "@id": "…/v3/autocomplete",     "@type": "SearchAutocompleteService" },
    { "@id": "…/v3/autocomplete",     "@type": "SearchAutocompleteService/3.0.0-beta" },
    { "@id": "…/v3/registration/",    "@type": "RegistrationsBaseUrl" },
    { "@id": "…/v3/registration/",    "@type": "RegistrationsBaseUrl/3.0.0-beta" },
    { "@id": "…/v3/registration/",    "@type": "RegistrationsBaseUrl/3.4.0" },
    { "@id": "…/v3/registration/",    "@type": "RegistrationsBaseUrl/3.6.0" },
    { "@id": "…/v3/flatcontainer/",   "@type": "PackageBaseAddress/3.0.0" },
    { "@id": "…/v3/publish",          "@type": "PackagePublish/2.0.0" },
    { "@id": "…/v3/symbolpublish",    "@type": "SymbolPackagePublish/4.9.0" }
  ],
  "@context": { "@vocab": "http://schema.nuget.org/services#", "comment": "http://www.w3.org/2000/01/rdf-schema#comment" }
}
```

**Registration index** `v3/registration/{id}/index.json`. All pages inline (no separate page
documents) until a package has more than 128 versions; then page out exactly as nuget.org does.

```json
{
  "@id": "…/registration/{id}/index.json",
  "@type": [ "catalog:CatalogRoot", "PackageRegistration", "catalog:Permalink" ],
  "count": 1,
  "items": [ {
    "@id": "…/registration/{id}/index.json#page/{lower}/{upper}",
    "@type": "catalog:CatalogPage",
    "count": 2, "lower": "1.0.0", "upper": "2.0.0",
    "parent": "…/registration/{id}/index.json",
    "items": [ {
      "@id": "…/registration/{id}/{version}.json",
      "@type": "Package",
      "commitId": "<guid>", "commitTimeStamp": "<iso8601>",
      "catalogEntry": {
        "@id": "…/registration/{id}/{version}.json",
        "@type": "PackageDetails",
        "authors": "…", "description": "…", "iconUrl": "…", "id": "…", "language": "",
        "licenseUrl": "…", "licenseExpression": "", "listed": true, "minClientVersion": "",
        "packageContent": "…/flatcontainer/{id}/{version}/{id}.{version}.nupkg",
        "projectUrl": "…", "published": "<iso8601>", "requireLicenseAcceptance": false,
        "summary": "", "tags": [ "…" ], "title": "", "version": "1.0.0",
        "dependencyGroups": [ {
          "@id": "…#dependencygroup", "@type": "PackageDependencyGroup", "targetFramework": "",
          "dependencies": [ { "@id": "…#dependency", "@type": "PackageDependency", "id": "…", "range": "[1.0.0, )", "registration": "…/registration/{dep}/index.json" } ]
        } ]
      },
      "packageContent": "…/flatcontainer/{id}/{version}/{id}.{version}.nupkg",
      "registration": "…/registration/{id}/index.json"
    } ]
  } ],
  "@context": { "@vocab": "http://schema.nuget.org/schema#", "catalog": "http://schema.nuget.org/catalog#" }
}
```

**Trap, verified in source.** The PackageManagement NuGet provider (`NuGetPackageFeed3.cs`,
OneGet/NuGetProvider on GitHub) flattens JSON so that `@type` becomes `Metadata.type` and then
reads `root.Metadata.type` and `packageEntry.catalogentry`. BaGet omitted `@type` on the
registration root and the provider crashed with a NullReference (BaGet issues 199 and 427,
OneGet issue 430). Every `@type` above is mandatory.

**Which provider the fleet runs (verified 2026-09-11).** PackageManagement 1.4.8.1 bundles NuGet
provider **3.0.0.1**, which is the v3 code above, and selects it over a separately installed 2.8.5.208
(the older DLL is v2 only). So on Windows PowerShell 5.1 with PowerShellGet 2.2.5 the `@type` markers
matter.

**Catalog entry document (found in phase 1 against provider 3.0.0.1).** To resolve a version the
provider follows the leaf's `catalogEntry` URL and reads `version` and the metadata from that
document. It must be the package details, not the leaf again; otherwise every version silently fails
to match and `Find-Package -Name X`, `Save-Package` and `Install-Package` report "no match". FiGet
serves one document per version at `v3/catalog/{id}/{version}.json`, and the inline
`catalogEntry.@id` names the same URL. The provider also asks for shortened version spellings first
(`1.0.json` before `1.0.0.json`), so leaf and catalog routes normalise the version.

**Registration leaf** `{version}.json`: `@id`, `@type: ["Package", "http://schema.nuget.org/catalog#Permalink"]`, `catalogEntry` (same object), `listed`, `packageContent`, `published`, `registration`.

**Flat container**: `{id}/index.json` → `{ "versions": [ "1.0.0", "1.1.0-beta1" ] }` (lower-case,
normalised, ascending); `.nupkg` and `.nuspec` streams with correct content types and
`Content-Length`.

**Search** `v3/query`: response `{"@context":{"@vocab":"http://schema.nuget.org/schema#","@base":"…/registration/"},"totalHits":n,"data":[{"@id":"…/registration/{id}/index.json","@type":"Package","registration":"…","id":"…","version":"<latest>","description":"…","summary":"","title":"","iconUrl":"…","licenseUrl":"…","projectUrl":"…","tags":[…],"authors":[…],"owners":[],"totalDownloads":0,"verified":false,"packageTypes":[{"name":"Dependency"}],"versions":[{"version":"1.0.0","downloads":0,"@id":"…/registration/{id}/1.0.0.json"}]}]}`.
`q` supports free text and the `id:`, `tags:`, `packageid:` prefixes PSResourceGet uses.
`packageType` filter and `semVerLevel=2.0.0` are accepted; semVer 1 clients never see semVer 2
versions.

**Autocomplete**: `{ "totalHits": n, "data": [ "id", … ] }`; with `id=` returns versions.

**Publish** `PUT v3/publish`: multipart with a single file part (any name), `X-NuGet-ApiKey`
required unless the feed allows anonymous push (never by default). 201 on success, 409 if the
version exists and overwrite is off, 400 with a message on a bad nupkg. `DELETE
v3/publish/{id}/{version}` unlists (or hard-deletes per feed setting); `POST` relists.

**Symbols**: `.snupkg` accepted on `symbolpublish`; PDBs served under `/symbols/{file}/{key}/{file}`
with the key computed as the symbol server expects (signature + age, upper-case hex).

### 4.3 NuGet v2 OData

There is no public specification. The contract is **what the clients send**, recorded in phase
0 and kept as fixtures. What is known before recording:

**Source validation probe (verified, phase 1).** The NuGet providers (2.8.5.208 and 3.0.0.1 both
contain it) validate a v2 source by requesting `{source}/FindPackagesById()?id='FoooBarr'`. Anything
but a success status makes the source "not valid". The v2 root must answer it with an empty feed
(200) on every root alias, including a source URL registered with a trailing slash. The reference
server does exactly that (recorded in phase 0).

**Service document** at the v2 root, `application/xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<service xml:base="{root}/" xmlns="http://www.w3.org/2007/app" xmlns:atom="http://www.w3.org/2005/Atom">
  <workspace><atom:title type="text">Default</atom:title>
    <collection href="Packages"><atom:title type="text">Packages</atom:title></collection>
  </workspace>
</service>
```

**`$metadata`**: a static EDMX describing entity `V2FeedPackage` (key `Id`, `Version`) with the
property set below and function imports `Search`, `FindPackagesById`, `GetUpdates`. Copy the
shape from Gitea's implementation (`routers/api/packages/nuget/api_v2.go`) or NuGet.Server;
both are MIT/Apache. Clients that fetch it only check that it parses.

**Entry** shape (Atom + OData properties). Every property below is emitted, empty when unknown,
with the `m:type` attributes shown; PowerShellGet reads `Dependencies`, `Tags`,
`NormalizedVersion`, `IsLatestVersion`, `IsAbsoluteLatestVersion`, `IsPrerelease`,
`Published`, `PackageHash`, `PackageHashAlgorithm`, `PackageSize`, and the `content src`.

```xml
<entry xml:base="{root}" xmlns="http://www.w3.org/2005/Atom"
       xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices"
       xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
  <id>{root}/Packages(Id='{Id}',Version='{Version}')</id>
  <category term="NuGetGallery.OData.V2FeedPackage" scheme="http://schemas.microsoft.com/ado/2007/08/dataservices/scheme"/>
  <link rel="edit" href="Packages(Id='{Id}',Version='{Version}')"/>
  <title type="text">{Id}</title>
  <updated>{LastUpdated}</updated>
  <author><name>{Authors}</name></author>
  <content type="application/zip" src="{root}/package/{Id}/{Version}"/>
  <m:properties>
    <d:Id>…</d:Id><d:Version>…</d:Version><d:NormalizedVersion>…</d:NormalizedVersion>
    <d:Authors>…</d:Authors><d:Copyright>…</d:Copyright>
    <d:Created m:type="Edm.DateTime">…</d:Created>
    <d:Dependencies>{Id}:{range}:{tfm}|{Id}:{range}:{tfm}</d:Dependencies>
    <d:Description>…</d:Description>
    <d:DownloadCount m:type="Edm.Int64">0</d:DownloadCount>
    <d:GalleryDetailsUrl>…</d:GalleryDetailsUrl><d:IconUrl>…</d:IconUrl>
    <d:IsLatestVersion m:type="Edm.Boolean">true</d:IsLatestVersion>
    <d:IsAbsoluteLatestVersion m:type="Edm.Boolean">true</d:IsAbsoluteLatestVersion>
    <d:IsPrerelease m:type="Edm.Boolean">false</d:IsPrerelease>
    <d:Language>…</d:Language>
    <d:LastUpdated m:type="Edm.DateTime">…</d:LastUpdated>
    <d:Published m:type="Edm.DateTime">…</d:Published>
    <d:PackageHash>{base64 sha512}</d:PackageHash><d:PackageHashAlgorithm>SHA512</d:PackageHashAlgorithm>
    <d:PackageSize m:type="Edm.Int64">…</d:PackageSize>
    <d:ProjectUrl>…</d:ProjectUrl><d:ReleaseNotes>…</d:ReleaseNotes><d:ReportAbuseUrl>…</d:ReportAbuseUrl>
    <d:RequireLicenseAcceptance m:type="Edm.Boolean">false</d:RequireLicenseAcceptance>
    <d:Summary>…</d:Summary><d:Tags>…</d:Tags><d:Title>…</d:Title>
    <d:VersionDownloadCount m:type="Edm.Int64">0</d:VersionDownloadCount>
    <d:MinClientVersion>…</d:MinClientVersion>
    <d:LastEdited m:type="Edm.DateTime" m:null="true"/>
    <d:LicenseUrl>…</d:LicenseUrl><d:LicenseNames>…</d:LicenseNames><d:LicenseReportUrl>…</d:LicenseReportUrl>
    <d:Listed m:type="Edm.Boolean">true</d:Listed>
  </m:properties>
</entry>
```

`Dependencies` is `id:range:targetFramework` triples joined by `|`; an empty range means any
version. PowerShellGet's own metadata (`PSModule`, `PSFunction_*`, `PSCommand_*`,
`PSDscResource_*`, `PSIncludes_*`, `PSVersion:…`) rides in `Tags` and must round-trip byte
for byte.

**Feed** wrapper: `<feed>` with `<m:count>` when `$inlinecount=allpages`, `<link rel="next">`
for paging, `$top` capped at a configurable maximum (nuget.org uses 100; PowerShellGet asks
for thousands and follows `next`).

**Query operations and parameters** to support, all case-sensitive on the OData side:

- `Packages()`: `$filter`, `$orderby`, `$skip`, `$top`, `$inlinecount`, `$select` (ignore
  `$select`, return everything).
- `Packages(Id='x',Version='y')`: single entry or 404.
- `FindPackagesById()?id='x'`: plus `$filter`, `$orderby`, `$skip`, `$top`, `semVerLevel`.
- `Search()?searchTerm='x'&targetFramework=''&includePrerelease=false`: plus `$filter`,
  `$orderby`, `$skip`, `$top`, `$inlinecount`, `semVerLevel`. `searchTerm` may carry
  PowerShellGet's `tag:` syntax; record it.
- `GetUpdates()?packageIds='a|b'&versions='1|2'&includePrerelease=&includeAllVersions=&targetFrameworks=&versionConstraints=`.
- `/$count` suffix on `Packages()`, `FindPackagesById()`, `Search()` returns `text/plain` integer.

**`$filter` grammar** (hand-written recursive-descent parser, no OData library):

```
expr     := or
or       := and ( 'or' and )*
and      := not ( 'and' not )*
not      := 'not' not | primary
primary  := '(' expr ')' | comparison | boolprop | func
comparison := operand ( 'eq' | 'ne' | 'gt' | 'ge' | 'lt' | 'le' ) operand
operand  := prop | string | number | 'true' | 'false' | 'null' | func
func     := 'substringof' '(' string ',' prop ')'
          | 'startswith' '(' prop ',' string ')'   | 'endswith' '(' prop ',' string ')'
          | 'tolower' '(' prop ')'                  | 'toupper' '(' prop ')'
          | 'indexof' '(' prop ',' string ')'
prop     := Id | Version | NormalizedVersion | Tags | Title | Description | Authors
          | IsLatestVersion | IsAbsoluteLatestVersion | IsPrerelease | Listed
          | Published | Created | LastUpdated | DownloadCount
```

Anything outside this grammar is a **400** whose body names the offending expression and whose
log line carries it at Warning. Never return an empty 200 for an unparsed filter; that is the
failure mode that makes `Find-Module` "find nothing" on the git forges.

`$orderby` supports the same properties with `asc|desc`, comma-separated. Default order is
`Id asc, Version desc`. Version comparison is `NuGetVersion` order, never string order.

**Push over v2**: nuget.exe (and therefore `Publish-Module` in PowerShellGet 2.x) PUTs a
multipart body to the source URL itself when the source does not end in `/api/v2`, and to
`{source}/package` when it does. Accept both, plus `/api/v2/package`. Header `X-NuGet-ApiKey`.

### 4.4 Asset directories

> Corrected 2026-09-13 against the reference client library and documentation; the table as built, with
> where each rule came from, is `docs/protocol-assets.md`. The sketch below was wrong in four places: `PUT`
> never replaces (`POST` does, and is what the client sends), a file `DELETE` answers 200 rather than 204
> and does not remove folders, folders are deleted through `POST /delete/{path}?recursive=` and created
> through `POST /dir/{path}`, and a listing item's `type` is the content type or `dir`, never `file`.

```
GET     /endpoints/{directory}/content/{path}    stream; ETag = sha256; Last-Modified; Range supported
HEAD    same, headers only
PUT     /endpoints/{directory}/content/{path}    raw body = file; creates folders; 201; never replaces
POST    /endpoints/{directory}/content/{path}    raw body = file; creates or replaces; 201
PATCH   /endpoints/{directory}/content/{path}    raw body = file; replaces only; 201
DELETE  /endpoints/{directory}/content/{path}    200, also when absent; files only
GET     /endpoints/{directory}/dir/{path}        JSON: [{ "name", "parent", "size", "type", "content", "created", "modified", "md5", "sha1", "sha256", "sha512", … }]
POST    /endpoints/{directory}/dir/{path}        create a folder; 201
POST    /endpoints/{directory}/delete/{path}     delete a file or folder; ?recursive=true for a full folder
GET     /endpoints/{directory}/metadata/{path}   one item as above
POST    /endpoints/{directory}/metadata/{path}   { "type", "userMetadataUpdateMode", "userMetadata", "cacheHeader" }
```

Auth: read is per-directory anonymous or token; write always a token or an authenticated UI session, never
anonymous, and that is not configurable. Optional per-directory
**remote URL caching**: a `PUT` with `X-Source-Url` (or the admin UI) fetches the URL once and
stores it under the path, so an installer can be "pinned" from a vendor site.

### 4.5 Management (compatibility) API

> Corrected 2026-09-13 against the reference client: `latest` answers a **list** (one entry per package), the
> version objects carry `purl`, `totalDownloads`, `size`, hashes and more, and the client also calls `GET
> /api/packages/{feed}` before a download or delete. As built: `docs/protocol-management.md`.

Minimum shape existing scripts already call against the commercial server:

```
GET  /api/packages/{feed}/versions?name={id}[&version={v}]   -> [ { "name", "version", "published", "publishedBy", "downloads", "listed" } ]
GET  /api/packages/{feed}/latest?name={id}                    -> single object as above, or 404
POST /api/packages/{feed}/delete?name={id}&version={v}        -> 200
```

Header `X-ApiKey`. Beyond that, FiGet's own admin API under `/api/v1/…` is free-form and
consumed by the Blazor UI.

---

## 5. Connector semantics (the rule that must not be broken)

This is the behaviour that actually hurts on the server being replaced: a module version is
cached locally, a newer one appears upstream, and the client is answered with **two** entries
for one id, each flagged latest from its own source. `Find-Module` cannot choose,
`Update-Module` fails, and meta-modules that pin exact dependency versions (`Microsoft.Graph`,
~40 sub-modules) flip between versions during dependency resolution because different
dependencies see different "latest" answers.

Rules, implemented once in `IVersionListService` and used by **every** listing endpoint (v2
`Packages()`, `FindPackagesById()`, `Search()`, `GetUpdates()`; v3 registration, flat
container, search, autocomplete; the management API):

1. For a package id, the version list is the **union** of local versions and the versions
   reported by the upstream that **owns** the id, de-duplicated by `NuGetVersion` normalised form.
   The owner is the first enabled upstream, in the feed's priority order, that allows the id and lists
   a version of it; upstreams after it are not asked about that id. An id with a version **pushed** to
   the feed has no upstream owner at all: it is served only from the feed, unless the feed sets
   `MergePushedIdsWithUpstreams` (see rule 10). (Amended 2026-09-13, from testing.)
2. `IsLatestVersion` (highest stable) and `IsAbsoluteLatestVersion` (highest incl. prerelease)
   are computed **after** the merge and are true on exactly one entry each.
3. Upstream version lists are cached per (upstream, id) with a short TTL (default 5 minutes,
   configurable) in the database, so every replica sees the same answer. A cache miss or expiry
   queries upstream synchronously with a bounded timeout; on upstream failure the last known
   list is served and a Warning is logged, never an error to the client.
4. Any (id, version) that exists upstream is fetchable on demand: the download endpoints
   look through for exact versions, not only the latest. On first download the nupkg is
   stored locally, indexed, and from then on served locally.
5. A locally cached copy is never dropped because upstream moved on. Retention rules apply to
   curated content; cached content is pruned only by explicit cache policy: not downloaded for N
   days, or older than a cache age. Every download records a last-download time per version for
   this, so a version in use is never pruned. The cache policy never touches pushed versions. (A
   total-size cap was planned and dropped by the owner on 2026-09-15: age and use already bound the
   cache, and the volume's size is watched where the volume is.)
6. **A cached copy of a version the upstream has withdrawn stops being offered.** When an upstream
   answers and no longer lists a version that exists here as a cached copy, that copy is unlisted:
   it disappears from listings, can never be "latest", and is never what an install of the newest
   version picks up. It is unlisted rather than deleted, so a deployment already pinned to that
   exact version can still fetch it while it is moved off. If the upstream offers it again, it is
   listed again. This applies only to cached copies, never to what was pushed to the feed, and only
   when an upstream actually answered: an outage serves the last known list and withdraws nothing.
7. Search on a proxy feed queries local metadata first and, when the query is a name lookup
   (`Id eq`, `packageid:`, `FindPackagesById`), also asks upstream so uncached packages are
   findable. Free-text search fans out to upstreams too, always, bounded per upstream: that is what
   makes a wildcard `Find-Module` find a module nobody has cached. (Planned as opt-in per feed;
   changed to always by the owner on 2026-09-15, matching what was built.)
8. Allow and deny lists per upstream (regex on id) are applied before anything upstream is
   listed or fetched.
9. ~~If a feed has several upstreams and both hold the same (id, version), the first upstream in
   feed order wins for the download; metadata is identical by definition.~~ Superseded 2026-09-13:
   "identical by definition" does not hold. Two galleries can hold **different** packages under one
   name, and merging them per version let the higher version of either become latest. The owner from
   rule 1 serves the whole id, versions and downloads alike. While an upstream ahead of the owner
   cannot be asked and nothing about the id is remembered, nothing is served from the upstreams below
   it: once cached, a wrong package would stay. The priority order is editable in the admin UI.
10. **A pushed id owns its name.** Pushing a version of an id makes the feed serve that id only from
   what it holds. Copies of the id cached from an upstream before the push are unlisted, not deleted,
   so a pinned deployment can still fetch them. The push answers with `X-NuGet-Warning` when an
   upstream also holds the id; nuget, dotnet and PSResourceGet print it. A feed that wants the old
   merge sets `MergePushedIdsWithUpstreams`, and the warning then says the two are merged.

Two named acceptance tests (§7.2): the cached-v1/upstream-v2 scenario and the
`Microsoft.Graph` install and update with a partially warm cache.

---

## 6. Phases, deliverables, acceptance

Estimates are for one experienced .NET developer.

### Phase 0 — record the contract (2–3 days)

Put a logging reverse proxy (any: YARP with request/response capture, mitmproxy) in front of a
known-good server and run every scenario in §7.2 with every client. Store each request
(method, path, query, headers minus auth) and response (status, headers, body) as a fixture
under `tests/fixtures/<client>/<scenario>/`. **Scrub** package ids, hostnames and tokens to
synthetic values before committing; a scrub script is part of the deliverable.

Deliverable: fixtures + `docs/protocol-v2.md` listing the exact query strings each client
emits. Acceptance: every scenario in §7.2 has at least one fixture.

### Phase 1 — core, persistence, storage, v3 (1.5–2 weeks)

- Solution skeleton (§3), CI on GitHub Actions (Linux: build, test on SQLite; SQL Server via
  a service container).
- Entities and both migration assemblies. `dotnet ef migrations add` documented in
  `docs/configuration.md`.
- `IPackageIndexer` using `NuGet.Packaging` to read a nupkg stream into `PackageVersion` +
  dependencies; sha512; size.
- FileSystem storage, content-addressed layout `packages/{id-lower}/{version-lower}/{id}.{version}.nupkg` + `.nuspec`.
- Full v3 surface of §4.2 for local (curated) feeds. Push with API key.
- Minimal Blazor UI: list feeds, browse packages, create API key.
- `deploy/Dockerfile` (non-root, 8080, `/data`), `compose.example.yml` with SQLite.

Acceptance: `dotnet nuget push`, `dotnet add package` / restore, `nuget.exe push/search/install/delete`,
`Publish-PSResource`, `Find-PSResource`, `Save-PSResource` against the v3 URI all pass
(§7.2 scripts). The registration JSON carries every `@type` marker (integration test).
PackageManagement 1.4.8.1 with its NuGet provider 3.0.0.1: `Find-Package` by exact name, all
versions, wildcard, `Save-Package` and `Install-Package` against the v3 URI. nuget.exe 7.x refuses
`list` for v3 sources and PSResourceGet refuses wildcard and tag searches for v3 repositories; those
are client limits, tested on v2.

### Phase 2 — v2 OData (1.5–2 weeks)

- Service document, `$metadata`, entry/feed writers, `$filter`/`$orderby` parser, all
  operations in §4.3, push and delete.
- Fixture tests replay phase 0 recordings and diff bodies structurally (XML canonicalised,
  timestamps and hostnames masked).

Acceptance: Windows PowerShell 5.1 + PowerShellGet 2.2.5 + PackageManagement 1.4.8.1 passes
every §7.2 scenario against `/nuget/{feed}/`; PSResourceGet passes against `/api/v2`.
**This is the milestone at which the repository goes public.**

### Phase 3 — proxy feeds and the management API (1 week)

- `FeedUpstream` (v2 and v3 via `NuGet.Protocol`'s `SourceRepository`), `IConnectorService`,
  `IVersionListService` per §5, cached upstream index table, allow/deny lists.
- Management API §4.5.
- Optional drop-folder importer (watch a directory, index and move nupkgs).

Acceptance: the two named connector tests (§7.2) plus all phase 1–2 scenarios unchanged.

### Phase 4 — asset directories and the browse UI (4–5 days)

§4.4 in full, Range requests, UI upload and browse, remote-URL pinning.

Real asset directories hold installers well over 100 MB (a surveyed production directory holds
eleven files, the largest 118 MB, about 1 GiB in total). So `MaxAssetSizeMB` defaults to 1024,
uploads stream to a temporary file instead of being buffered in memory, and the asset `PUT` raises
the per-request body limit through `IHttpMaxRequestBodySizeFeature` exactly as `PackageUpload`
already does; otherwise Kestrel's 30 MB default rejects the upload before the handler sees it.
Downloads must stream too, and honour `Range` so a resumed `win_get_url` works.

**Uploading through the UI is a first-class path, not a convenience.** Files reach an asset directory
by drag and drop onto the browse page, or by picking them, with a progress indicator and overwrite
confirmation. The pages are static SSR, so the drop zone posts straight to the asset endpoint with a
small script (`fetch` and `FormData`, or a `PUT` per file) rather than through an interactive
component; the server path is the same one a script uses. Uploading always requires an authenticated
session or a token, whatever the directory's anonymous-read setting says.

**The UI is how a person uses the server, so it carries the same affordances the commercial servers
have**, and this phase finishes them:

- Search a feed and open any package, including one that only exists on an upstream.
- Download any version straight from the browser, both from the package page and from an
  all-versions list, without pasting a command anywhere.
- Usage instructions on the package page as tabs with a copy button, chosen by feed kind:
  `Register-PSRepository` and `Install-Module` for the PowerShell clients still on v2,
  `Register-PSResourceRepository` and `Install-PSResource` for PSResourceGet, `dotnet add package`
  and a `PackageReference` line for .NET, and for an asset directory a plain URL, a `curl` line and
  a PowerShell download line.
- Every snippet is a **per-feed template**, seeded with the built-in defaults and editable in the
  UI, with placeholders `{feedUrl}`, `{feedName}`, `{id}`, `{version}` and `{path}`. A feed may add
  or remove snippets, so a team can use its own wording, or name the hostname its clients actually
  reach, which is rarely the one the server sees.

Acceptance: `curl`, `Invoke-WebRequest`, and Ansible `win_get_url` fetch by path; `PUT` with a
token stores; listing JSON matches `docs/protocol-assets.md`.

### Phase 5 — identity, authorisation, hardening (1 week)

- OIDC: N named schemes from `Auth:Oidc:[]` (name, authority, clientId, clientSecret
  reference, scopes, roleClaim, groupToRole map, emailAllowList). Login page lists providers.
- Roles: Reader, Publisher, FeedAdmin, Admin; per-feed anonymous read flag; per-directory
  read flag.
- API keys and PATs: random 32 bytes, base64url, shown once, stored as SHA-256; scope = feed
  or directory + permission + expiry; last-used tracking; revoke.
- Audit log: package push, unlist, delete, asset upload and delete, key create and revoke, feed and
  directory config change, retention and cache-prune runs. Each entry records who, what, when and
  from which address, is queryable per feed in the UI, and is itself subject to a retention setting.
- Usage reporting: download count and last-download time per version, per feed totals, most and
  least used packages, and cached versions nobody has fetched in N days. Readable in the UI and as
  JSON, and the same numbers the cache policy and retention rules act on, so a purge can be
  previewed before it runs.
- Hardening: upload size limit, nupkg validation (zip bomb guard, nuspec required,
  id/version match), hash verification on cache fill, rate limit on anonymous endpoints,
  retention job, health endpoints, OpenTelemetry.

Acceptance: login through two different providers in one instance (e.g. Authentik and
Google, or Entra), role mapping observed in the UI, all client scenarios still pass with
anonymous read off and a PAT in use.

### Phase 6 — cluster deployment and migration (1 week)

- S3-compatible storage implementation; leader-locked background jobs (retention, cache
  refresh) or CronJob mode (`figet jobs run retention`).
- Helm chart: Deployment (2 replicas), Service, Ingress/Route, ConfigMap, Secret refs, PVC or
  S3 config, CronJobs, probes, `securityContext` for arbitrary UID.
- Migration tool `figet import` that walks another server's v2 feed (`Packages()?$skip=…`) and
  its asset directory, and pushes into FiGet. A version whose hash matches the same (id, version)
  on one of the feed's upstreams is imported as cached, not pushed, so the cache policy applies to
  it. The source server's publisher field is not reliable for telling the two apart.

Acceptance: two replicas behind one ingress pass the full §7.2 matrix; a migration from a
populated source server reproduces every (id, version) and every asset with matching hashes.

---

## 7. Verification

### 7.1 Fixture tests

`FiGet.Protocol.Tests` loads each fixture, replays the request through
`WebApplicationFactory` against a SQLite-backed instance seeded with the synthetic packages
the fixture set needs, and compares the response body structurally (JSON: property set and
values with `@id`/timestamps masked; XML: canonicalised, same masking). A fixture is the
specification; changing behaviour means changing the fixture first.

### 7.2 Compatibility matrix (real clients)

Scripts in `tests/FiGet.Compat/`, one per client, each taking the feed URL and a token,
runnable on a developer's Windows machine and, after the repo is public, on a Windows GitHub
runner. Each script registers the feed, then runs the scenarios and asserts the installed
manifest or the pushed version.

| Client | Root used | Scenarios |
|---|---|---|
| Windows PowerShell 5.1, PowerShellGet 2.2.5, PackageManagement 1.4.8.1 (NuGet provider 3.0.0.1) | `/nuget/{feed}/` | `Register-PSRepository`; `Find-Module -Name X`; `-Name *`; `-Tag`; `-AllVersions`; `Install-Module X`; `-RequiredVersion`; `-AllowPrerelease`; `Update-Module`; `Save-Module`; `Publish-Module`; `Install-Package -ProviderName NuGet`; `Find-Package -AllVersions` |
| PowerShell 7 + PSResourceGet | `/api/v2` and `/v3/index.json` | `Register-PSResourceRepository` (auto-detect); `Find-PSResource`; `-Version` ranges; `Install-PSResource`; `Update-PSResource`; `Save-PSResource`; `Publish-PSResource` |
| nuget.exe | v2 and v3 | `list`, `install`, `push`, `delete` |
| dotnet CLI | v3 | `add package`, `restore`, `nuget push` |
| Ansible | v2 + assets | `win_psrepository`, `win_psmodule`, `win_get_url` |
| **Connector A** | v2 + v3 | seed cache with v1 of a synthetic module, publish v2 upstream (a second FiGet instance acts as upstream): `Find-Module` returns one entry at v2; `Install-Module` installs v2; `Update-Module` moves v1 to v2 |
| **Connector B** | v2 + v3 | `Install-Module Microsoft.Graph` then `Update-Module Microsoft.Graph` against a proxy feed whose cache holds a mixed set of sub-module versions: one consistent pinned closure, no version flip |

Clients are pinned by version in the scripts; the PowerShell 5.1 module versions are the ones
Windows fleets are typically frozen at, and they are installable from the PowerShell Gallery
for a test machine.

### 7.3 Load sanity

`Find-Module -Name *` over a feed of 3,000 packages completes within the time the reference
server takes (measure once in phase 0, keep the number in `docs/protocol-v2.md`).

---

## 8. Configuration model (grows with the phases)

```
FiGet:
  Database:       Provider: SqlServer|Sqlite ; ConnectionString
  Storage:        Provider: FileSystem|S3 ; Root: /data ; S3: { Endpoint, Bucket, Region, AccessKeyRef, SecretKeyRef, ForcePathStyle }
  PublicBaseUrl:  https://packages.example.org        (used in every absolute URL the protocols emit)
  Feeds:          declared in the database, seeded from config on first start:
                  - Name, Type: Curated|Proxy, AnonymousRead, AllowOverwrite, DeletionBehavior: Unlist|HardDelete,
                    Retention: { MaxMajor, MaxMinor, MaxPatch, MaxPrerelease, KeepUsedWithinDays, DryRun }, Instructions: [ { Name, Template } ], Cache: { PruneUnusedAfterDays, MaxSizeMB }, Upstreams: [ { Url, Kind: V2|V3, Allow: [regex], Deny: [regex], AuthRef } ]
  Assets:         directories declared the same way: Name, AnonymousRead
  Auth:           Oidc: [ { Name, Authority, ClientId, ClientSecretRef, Scopes, RoleClaim, GroupToRole: {…}, EmailAllowList: [...] } ]
                  BootstrapAdminToken (first run only; printed once if unset)
  Limits:         MaxPackageSizeMB, MaxAssetSizeMB, MaxPageSize
  Connector:      UpstreamIndexTtl: 00:05:00 ; UpstreamTimeout: 00:00:10
```

Secrets are referenced (`…Ref`) and resolved from environment variables or mounted files,
never inline.

---

## 9. Known traps and decisions, so nobody rediscovers them

- **The server does not decide which version a client should install.** A proxy feed offers every
  version its upstreams have, and the client picks. There is deliberately no per-feed version filter,
  even though the case for one is easy to state: a module whose 2.x needs PowerShell 7 while the fleet
  is on 5.1. That is the module's compatibility problem, and a package server that silently hides
  versions to work around it makes a second, worse problem, because the feed no longer shows what the
  upstream actually has. A fleet that must stay on an older major version pins it with
  `-RequiredVersion`, or uses a curated feed holding only what it approves. Decided 2026-09-12, and the
  module that raised it fixed its own manifest soon after, which is how it should be settled.
- **What the server owes that choice is the metadata to make it.** A client decides from the package's
  own tags, `PSEdition_Desktop` against `PSEdition_Core`, so every version a feed lists carries the
  description, authors and tags the upstream published, including versions nobody has downloaded yet.
  Listing an uncached version with blank metadata looks harmless and is not: it removes exactly the
  signal the client needs.
- **`@type` everywhere in v3 registration JSON** (see §4.2). Non-negotiable, for the 3.x
  PackageManagement provider.
- **PackageManagement 1.4.8.1 uses its bundled NuGet provider 3.0.0.1**, not an installed 2.8.5.208.
  Load the fleet modules through `PSModulePath` when testing; importing them by path leaves the inbox
  PowerShellGet provider in charge and every 2.x cmdlet fails on `AllowPrereleaseVersions`.
- **A v2 source is validated with `{source}/FindPackagesById()?id='FoooBarr'`.** Answer it with an
  empty 200 feed, or the source is rejected before any real query.
- **A registration leaf's `catalogEntry` must resolve to the package details** (see §4.2), or provider
  3.0.0.1 finds nothing by name while "all versions" still works.
- **PowerShellGet 2.2.5 publishes with the dotnet CLI when it finds one**, otherwise with NuGet.exe
  4.1 or later (older copies are rejected for packing). Current SDKs and nuget.exe 7.x refuse to push
  to plain HTTP, so HTTP test setups need a nuget.exe before 7.0 and no dotnet on PATH.
- **NuGet 7 clients refuse plain-HTTP sources** (push, and in the library even when called from code)
  unless the source sets `allowInsecureConnections`. The library reports this only through its logger:
  a push that "does nothing" is this. Tests push over raw HTTP; compatibility scripts write a
  `nuget.config` with the flag.
- **PSResourceGet refuses wildcard names and tag search on v3 repositories** before sending a request.
- **Empty 200 on an unparsed `$filter` is the git-forge bug.** 400 + log instead.
- **Version ordering is `NuGetVersion` order.** `10.0.0` > `9.0.0`; `1.0.0-beta` < `1.0.0`;
  four-part versions from PowerShell manifests (`1.2.3.4`) are legal and normalise to
  `1.2.3.4`, not `1.2.3`. `1.0` normalises to `1.0.0`. Store both original and normalised.
- **PSResourceGet detects the API by URI suffix**: `/api/v2`, `/v3/index.json`, `/nuget`.
  Anything else fails registration or falls back badly. That is why the feed has three roots.
- **nuget.exe pushes to the source URL itself** on v2. Accept `PUT` on the feed root.
- **PowerShellGet metadata lives in `Tags`.** Round-trip untouched, including `PSVersion:x.y`
  and `PSGuid:…`, and keep `Tags` space-separated exactly as published.
- **PowerShellGet asks for `$top` in the thousands** on `Find-Module *`. Cap and page with
  `<link rel="next">`; the client follows it.
- **Two entries flagged latest for one id breaks every PowerShell client.** §5.
- **OpenShift runs the container as a random UID** in the root group. Files under `/data`
  must be group-writable; the image must not `USER 1000` and expect to stay 1000. Use
  `chmod -R g=u` in the Dockerfile and never write elsewhere.
- **SQLite and multiple replicas do not mix.** Refuse to start with `Provider: Sqlite` and
  `Replicas > 1` announced via config, and document it.
- **Full-text search differs per provider.** Search is `LIKE` over indexed lower-cased
  columns plus a tags column; no provider full-text features.
- **`Microsoft.Identity.Web` is Entra-specific.** Generic `OpenIdConnect` handler only.
- **Entra sends `groups` only when the app registration is configured for it**, and switches
  to an overage claim above 200 groups. Document the mapping; do not special-case it in code.
- **Google sends no groups.** Email allow-list is the fallback and must exist for that reason.
- **Images are published from a tag only**, by the `publish` job in `ci.yml`; a push to main builds the
  image and smoke-tests it without pushing.

---

## 10. Working method for the implementing agent

1. Read `CLAUDE.md`, then this file, then `docs/protocol-*.md` as they exist.
2. Work phase by phase. Do not start the next phase before the acceptance criteria of the
   current one are demonstrably met; write the demonstration down in `docs/status.md` with the
   date, the command run, and the result.
3. Every protocol change starts with a fixture. Every bug found with a real client becomes a
   fixture before it is fixed.
4. Keep `docs/configuration.md` in step with `FiGetOptions`; a key that is not documented does
   not exist.
5. Report outcomes faithfully. A test that was skipped is reported as skipped. A client that
   was not run is reported as not run.
