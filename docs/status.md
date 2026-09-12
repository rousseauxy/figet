# Status

What is built, how it was demonstrated, and what is known to be missing. Newest first. A check is only
listed as passed when it was run against the code in the commit it names.

---

## Phase 0: record the contract — started 2026-09-11

**State: recordings and fixtures for the three v2 clients done; phase 2 can start from them.**

| Recording | Client | Exchanges | Fixtures |
|---|---|---|---|
| `powershellget-2.2.5` | Windows PowerShell 5.1, PowerShellGet 2.2.5, PackageManagement 1.4.8.1 (NuGet provider 3.0.0.1), NuGet.exe 6.11.1 | 419 | 36 scenarios |
| `nugetexe-6.11.1` | nuget.exe 6.11.1 | 25 | 13 scenarios |
| `psresourceget-1.2.0-v2` | PSResourceGet 1.2.0 against a feed root without `/api/v2` | 4 | 4 scenarios |
| `psresourceget-1.2.0-v2-gallery` | PSResourceGet 1.2.0 in v2 mode against the PowerShell Gallery | 16 | 12 scenarios |

- **Reference server**: its free edition in one container on a home server: a curated PowerShell feed, a
  PowerShell feed with a PowerShell Gallery connector, a PowerShell feed and a NuGet feed for the other clients,
  and an asset directory. A private instance means the recordings contain no organisation data.
- **Tools**: `tools/FiGet.Recorder` (recording reverse proxy; `--preserve-host false` for public upstreams) and
  `tools/FiGet.Fixtures` (reduces a recording to committed digests: request shapes, statuses, property sets,
  versions and flags; no response bodies, no hosts, no credentials, and it refuses to write anything that looks
  like an address, a user path or a secret).
- **Scripts**: `tests/FiGet.Compat/Record-PowerShellGetV2.ps1`, `Record-NuGetExeV2.ps1`,
  `Record-PSResourceGetV2.ps1`. Each restores what it touches; after every run `PSRepositories.xml` and the
  user's `NuGet.Config` were verified byte for byte against their original hashes, and no test package remained
  in the user's module folder or NuGet package cache.

**Main result: the double-latest failure reported on the server being replaced is reproduced and explained.**
With more versions than PowerShellGet's page size of 40 (Pester, Microsoft.Graph), the reference server puts a cached older
version in front of page 0 flagged latest while the connector's latest stays flagged on a later page, and drops
upstream versions at the page boundary. `Find-Module Microsoft.Graph` returned the cached 2.30.0 instead of
2.39.0 and `Save-Module Microsoft.Graph` failed with "multiple modules matched". Evidence and FiGet's answer are
in `docs/protocol-v2.md`, "Paging and latest flags".

Other findings, all in `docs/protocol-v2.md`: PowerShellGet 2.2.5 uses only five v2 routes and paginates by
`$skip` itself; nuget.exe adds `$metadata`, `Packages(Id,Version)`, `$orderby` and a delete route under the feed
root; PSResourceGet cannot use a feed URL without `/api/v2` for anything but publishing; the reference server
accepts duplicate pushes silently.

Client-side traps met while getting the scripts to run are in the build plan's traps list: PowerShellGet's
provider is found through `PSModulePath`; PackageManagement 1.4.8.1 uses its bundled NuGet provider 3.0.0.1;
PowerShellGet 2.2.5 publishes with the dotnet CLI when present and otherwise needs NuGet.exe 4.1 to 6.x over
HTTP; `Register-PSRepository` also writes the user's `NuGet.Config`; Windows PowerShell 5.1's manifest template
cannot take a prerelease label by text edit.

**Not recorded yet**: authenticated feeds, `Update-Module` across a page boundary, and the asset directory
and management API calls (their shapes are simple and are listed in build plan §4.4 and §4.5).

**Production comparison, 2026-09-11.** A read-only inventory of a production server on the same reference server version
showed the same failure with exact arithmetic: the entry total per module equals the upstream version count, so
each cached version is duplicated and hides one upstream version. Details in `docs/protocol-v2.md`, section
"Paging and latest flags".

### Phase 1 amendment — 2026-09-11

**Catalog entry documents.** Registration leaves pointed `catalogEntry` at themselves. PackageManagement's
NuGet provider 3.0.0.1 (the one Windows PowerShell 5.1 fleets actually use) resolves a version by reading
the package details from that URL, so `Find-Package -Name X`, `Save-Package` and `Install-Package` all
reported no match while "all versions" worked. FiGet now serves `v3/catalog/{id}/{version}.json`, and the
registration test follows the URL. Integration tests: 35 of 35 on SQLite and LocalDB. PackageManagement
1.4.8.1 against a running instance afterwards: exact name, all versions, wildcard, missing id, save and
install all behave as expected.

---

## Phase 1: core, persistence, storage, v3 — 2026-09-11

**State: done, with one acceptance item moved to phase 2 (see "Plan corrections").** CI on GitHub
Actions is green on commit `036acc9` (run 34632174594): 90 tests passed with none skipped on Ubuntu
against SQLite and a SQL Server 2022 service container, both migration assemblies match the model, and
the image builds and serves `/health/ready` and the service index when started as UID 1000123456 in
group 0. The first run failed only on the drift check, which passed `-c` (the EF context option) instead
of the build configuration.

### Delivered

| Plan item (§6 phase 1) | Where |
|---|---|
| Solution skeleton, central package versions, analyzers as errors | `figet.slnx`, `Directory.Build.props`, `Directory.Packages.props` |
| Entities and both migration assemblies | `src/FiGet.Core/Entities`, `src/FiGet.Persistence*`, `Initial` migration for SQL Server and SQLite |
| Package indexer on `NuGet.Packaging` (metadata, dependency groups, SemVer 2 detection, SHA-512, size) | `src/FiGet.Core/Packages/PackageIndexer.cs` |
| Filesystem storage, atomic writes, path guards | `src/FiGet.Storage/FileSystemPackageStorage.cs` |
| v3: service index, registration (inlined up to 128 versions, paged beyond), leaf, flat container, search, autocomplete, push, unlist/relist/hard delete, symbol publish, symbol server | `src/FiGet.Protocol.V3/NuGetV3Endpoints.cs` |
| The merged version list rule (§5), used by every listing endpoint already | `src/FiGet.Core/Versions/VersionListBuilder.cs` |
| Push with API key; token scopes Read / Push / Delete / Admin, optional feed scope, expiry, revoke | `src/FiGet.Core/Tokens`, `src/FiGet.Http/FeedAccess.cs` |
| Minimal UI: feeds (list, create, change settings, delete), packages (search, browse, versions, install snippets), tokens (create, revoke), a copy button on every source URL | `src/FiGet.Web/Components` |
| Dockerfile (non-root, arbitrary UID, port 8080, `/data`) and compose example with SQLite | `deploy/` |
| CI: build, EF model drift check, tests on SQLite and SQL Server, image build and arbitrary-UID start | `.github/workflows/ci.yml` |
| Compatibility scripts for the dotnet CLI and PSResourceGet | `tests/FiGet.Compat` |

### Evidence

Automated, on the commit that adds this file (run on Windows 11, .NET SDK 10.0.401):

| Suite | Result |
|---|---|
| `dotnet test`, whole solution, without `FIGET_TEST_SQLSERVER` | 90 total, 74 passed, 16 skipped (the SQL Server half) |
| `dotnet test`, whole solution, `FIGET_TEST_SQLSERVER` pointing at LocalDB | 90 total, 90 passed: 55 unit, 16 v3 tests on SQLite, 16 on SQL Server, 3 admin UI |
| `dotnet ef migrations has-pending-model-changes`, both providers | No changes |

The integration fixture asserts the active EF provider and the SQLite file location on start, so a
provider setting that silently fails to apply cannot produce a green SQL Server run. After the SQL
Server run, no `figet_*` databases were left on LocalDB.

Real clients against a locally running instance (`dotnet run`, SQLite, plain HTTP on 127.0.0.1):

| Client | Checks | Result |
|---|---|---|
| dotnet CLI (NuGet 7.9) via `tests/FiGet.Compat/Invoke-DotnetCliCompat.ps1` | pack; push 2 versions with `.snupkg` auto-pushed to `symbolpublish`; duplicate → 409 with the server's message; `--skip-duplicate`; wrong key → 403; `add package` picks latest stable; `--prerelease` picks the prerelease; restore from an empty package cache installs from FiGet; `package search` | all passed |
| PowerShell 7.6.5 + PSResourceGet 1.2.0 via `tests/FiGet.Compat/Invoke-PSResourceGetCompat.ps1` | repository auto-detected as V3; `Publish-PSResource`; duplicate → 409; `Find-PSResource` by name; `Save-PSResource`; import and run the saved module | all passed |
| nuget.exe 7.9.0 (manual) | `push`; `search`; `install` latest stable; `install -Version` prerelease; `delete` → version unlisted | all passed |
| nuget.exe 7.9.0 `list` | the client refuses `list` for v3 sources ("does not support listing packages") | client limitation |
| PSResourceGet wildcard name and tag search | the client refuses both for every v3 repository, as Microsoft documents | client limitation; v2 in phase 2 |
| Windows PowerShell 5.1 + PackageManagement 1.4.8.1 with its NuGet provider 3.0.0.1, against the v3 URI (added 2026-09-11, after the catalog entry fix) | `Find-Package` by exact name; all versions with prerelease; wildcard `Smoke*`; missing id → no match; `Save-Package -RequiredVersion`; `Install-Package` | all passed |
| Admin UI in a browser-like client | login refused for wrong and non-admin tokens, accepted for an admin token; pages behind sign-in | covered by `AdminUiTests` |

The NuGet 7 client refuses plain-HTTP sources unless the source entry sets `allowInsecureConnections`.
The compatibility scripts write such a `nuget.config` for themselves; production instances sit behind
HTTPS.

### Plan corrections found while building

1. **Corrected twice; this is the verified state.** A first test force-loaded the NuGet provider
   2.8.5.208 DLL, found it has no v3 client, and concluded the Windows PowerShell 5.1 fleet cannot use
   v3. That was wrong about the fleet: PackageManagement 1.4.8.1 bundles NuGet provider **3.0.0.1**
   and selects it over 2.8.5.208 (checked with the fleet module set on `PSModulePath`). Provider 3.0.0.1
   has a v3 client, and testing FiGet with it found a real bug: registration leaves pointed
   `catalogEntry` at themselves, while the provider reads the package details from that URL, so
   `Find-Package -Name X`, `Save-Package` and `Install-Package` all reported "no match". FiGet now
   serves a catalog entry document per version, and all of those pass. The acceptance item stays in
   phase 1. Both providers validate v2 sources with `FindPackagesById()?id='FoooBarr'`, which the v2
   root in phase 2 must answer.
2. **The admin UI uses static server rendering, not interactive Blazor Server.** Interactive circuits
   need sticky sessions across replicas; static pages with form posts need nothing, so the OpenShift
   two-replica target stays simple. The plan (§2) is corrected.
3. **A shared `FiGet.Http` project** holds feed resolution, credential extraction, public URL building
   and upload buffering, so v2 (phase 2) reuses them instead of copying from v3. The plan (§3) is
   corrected.

### Deviations and known gaps

- **Admin sign-in is an admin token pasted into a form** until OIDC arrives in phase 5. Cookies are
  HTTP-only, SameSite=Lax, and re-validated against the token (revoked or expired tokens end the
  session).
- **Data protection keys are stored unencrypted in the database** (the app logs a warning on start).
  Protecting them with a certificate belongs to phase 5 hardening.
- **Storage paths include the feed name**: `packages/{feed}/{id}/{version}/…`. Renaming a feed is not
  supported.
- **Autocomplete `totalHits`** is `skip + returned count`, not the full count. No known client relies on
  it.
- **Embedded icons, readmes and licence files** inside packages are not served; `iconUrl` and
  `licenseUrl` are passed through from the nuspec.
- **Upload size limit**: enforced while streaming (413); not yet covered by an automated test.
- **The UI browses and searches, but neither downloads nor uploads packages.** Package pages carry install
  snippets for the dotnet CLI and PSResourceGet only, hard-coded rather than per-feed templates,
  and there is no download link and no `Install-Module` snippet for the Windows PowerShell 5.1
  fleet. All of that is phase 4 in the build plan.
- **The container image** is not built on the development machine (no Docker there); CI builds it and
  starts it as UID 1000123456 in group 0 on every push. Since 2026-09-12 it also runs outside CI, on a
  Linux server, SQLite on a bind-mounted data directory: startup, feed seeding from configuration, the
  generated admin token, v3 push, search, flat container and unlist were all exercised over the network.
  One deployment note came out of it: a bind mount replaces the image's group-0 permissions on `/data`,
  so a bind-mounted deployment must run the container as a user that owns the mounted directory, while
  a named volume or an OpenShift arbitrary UID needs nothing.
- **Compatibility scripts** run manually; they join CI on a Windows runner once the repository is
  public (plan §7.2).

### Next

Phase 4: asset directories and the browse UI.

## Phase 3: proxy feeds — 2026-09-12

**State: a proxy feed merges its upstreams into one version list and caches on download, on both protocols.
The management API and the smaller connector extras are still open.**

### Delivered

| Plan item (section 5) | Where |
|---|---|
| `FeedUpstream` and `CachedUpstreamIndex`, with migrations for both providers | `src/FiGet.Core/Entities`, `src/FiGet.Persistence*/Migrations/*_Upstreams.cs` |
| Upstream client over NuGet's own library, so v2 and v3 upstreams both work | `src/FiGet.Core/Connectors/NuGetUpstreamClient.cs` |
| One merged version list across local and upstream versions, latest computed once | `ConnectorService` + `VersionListBuilder`, used by v2 `FindPackagesById()`/`Packages()` and v3 registration and flat container |
| Look-through download: any exact upstream version is fetched and cached on first request | `ConnectorService.EnsureCachedAsync`, v2 `package/{id}/{version}` and v3 flat container |
| Upstream version lists cached in the database with a time-to-live, shared by every replica | `EfUpstreamIndexStore`, `FiGet:Connector:UpstreamIndexTtl` |
| Allow and deny patterns per upstream, deny winning, with a match timeout | `ConnectorService.Allows` |
| Upstream credentials referenced by environment variable, never stored | `FeedUpstream.CredentialRef` |
| Feeds with upstreams declared in configuration | `FiGet:Feeds:N:Upstreams:M:*` in docs/configuration.md |
| Search reaches the upstreams, so a package nobody has cached is still findable | `ConnectorService.SearchUpstreamsAsync`, used by v2 `Search()`, v3 `query` and the browse UI |
| A cached copy of a version the upstream withdrew is unlisted, and listed again if it returns | `ConnectorService.ReconcileWithdrawnAsync` (section 5, rule 6) |

### Evidence

`dotnet test`: 128 total, 99 passed, 29 skipped (the SQL Server half). Nine proxy tests cover upstream-only
listings with exactly one latest flag, a local version and a newer upstream version merging into one list,
look-through download that caches and is then served locally, the cached listing being reused inside the
time-to-live, allow and deny patterns, an unreachable upstream falling back to its last known list, and the
v3 registration and flat container showing the same merged list.

### Decisions taken while building

- **A version that exists only upstream is listed with placeholder metadata.** A listing must show it before
  anything has been downloaded; the real nuspec replaces the row the moment the package is cached.
- **A local version always wins over the same version upstream**, which is what stops one package appearing
  twice, the failure that started this project.
- **A failing upstream serves its last known list** and logs a warning, because a stale list beats a failed
  install.
- **A feed that names upstreams is a proxy feed**, whatever the configured kind says.

### Verified against a real gallery

A proxy feed in front of the PowerShell Gallery was driven on the test instance on 2026-09-12. Pester came
back with its 144 versions, once each, with exactly one flagged latest: the case the server being replaced
gets wrong. A version was downloaded through look-through in about a second and served from the cache in
twenty-five milliseconds afterwards, and the cached row then carried the real hash, size and description
rather than the listing placeholder.

### Still open in phase 3

- The management API of section 4.5 (`/api/packages/{feed}/versions|latest|delete`).
- The v3 registration page, registration leaf and catalog entry still answer from local rows, so an
  upstream-only version is complete in the index but not addressable on its own leaf.
- Cache pruning by age or size, and the drop-folder importer.

## Browse UI and theming — 2026-09-12

Reported while using the test instance: search returned only what was cached, clearing the search box did
not restore the list, and there was no way to tell a pushed package from a cached one.

- **Search on a proxy feed now reaches the upstreams**, in the browse UI and in both protocols. Ids already
  held locally keep their local rows, so nothing appears twice.
- **A clear control** resets the search and the filter, instead of needing an empty search to be submitted.
- **Where a package came from is shown and filterable**: pushed here, cached, or upstream only. One feed
  still holds both, as on the server being replaced, but now you can tell them apart.
- **Theme packs**: a JSON file of token overrides compiled into a stylesheet served at
  `/themes/{name}.css` and layered after `app.css`, with an entity tag and a reload that needs no restart.
  The token names match the design system used by the other applications here, so a pack converts
  mechanically between them. `wwwroot/themes/graphite.json` is a worked example.

Also added, after using it: an admin can pull an upstream package into the feed from the search results,
and add or remove a feed's upstreams from its settings page. Both are plain form posts to admin-only
endpoints, because these pages are statically rendered and every one of those buttons changes something.

Since then, driven by using it:

- **The package page has tabs**, an overview with the newest versions and a full list, both showing where
  each version is: pushed here, cached, or held by an upstream. Every version links to its own page with
  the install lines, the metadata and the actions.
- **Download and pull are buttons.** A version held locally downloads; a version only upstream is pulled
  into the feed by an admin, which is the same fetch a client's first download would do.
- **Versions nobody has cached are described.** The upstream's description, authors and tags travel with
  them, which matters because a PowerShell client reads `PSEdition_Desktop` against `PSEdition_Core` to
  decide whether a version can run at all.
- **The full version list is paged**, fifty at a time, newest first. A gallery package can have two
  thousand versions, and rendering all of them was a download rather than a page.
- **A version has tabs**: Overview, Metadata, Dependencies, and Files for a version held here. Tags moved
  into Metadata as chips, with the generated ones folded away: a PowerShell module publishes one tag per
  exported command, so the list runs to thousands and reads as noise inline. Dependencies come from the
  stored package's own groups, and the file list is read from the package on demand. Two tabs the
  commercial server has are deliberately absent until the data behind them exists: History needs the audit
  log of phase 5, and usage needs the per-version download tracking the cache policy also wants. An empty
  tab teaches people not to click tabs.

### Next, in this order

1. **The design-system port.** The full token set and component classes, so a theme pack restyles every
   control rather than the handful of colours the base stylesheet defines today, and every page is
   rewritten onto those classes. First because everything built after it is then built once, in the
   final visual language, instead of being restyled later.
   Decided: packs are **YAML**, the same as the reference implementation, so a pack moves between the two
   applications unchanged. That means a YAML dependency here and converting the sample pack; the loader
   and the token names already match, only the parser changes.
   The port also covers the lists: the version list, the feed list and the search results get the same
   treatment as the record list in the reference application, rather than the plain tables they use now.
2. **An admin area with its own side menu.** The management controls sit among the public pages today.
   Feeds, tokens, upstreams and appearance belong behind one nav, leaving the public pages read-only.
3. **The themed dropdown.** The one used elsewhere is an interactive component and these pages are
   statically rendered, so it needs rebuilding as a script-driven listbox with the same look, unless the
   admin pages are made interactive instead. Last because it only matters once the admin forms exist.

Not doing, decided: per-feed version filtering (section 9). Still open from earlier phases: the packages
management API, replaying the recorded fixtures as tests, the real PowerShell 5.1 client run, per-version
registration leaves for upstream-only versions, cache pruning, promotion between feeds, and the two
deferred tabs above. The themed dropdown used elsewhere is an
interactive component, and these pages are statically rendered, so that one needs a decision before it can
be reused.

## Phase 2: v2 OData — 2026-09-12

**State: the surface is built and green against the recorded request shapes. Replaying the phase 0 fixtures
as tests is the one piece still missing before phase 2 is called done.**

### Delivered

| Plan item (section 4.3) | Where |
|---|---|
| Service document at the feed root and at the `/api/v2` alias, with and without a trailing slash | `src/FiGet.Protocol.V2/NuGetV2Endpoints.cs` |
| `$metadata` (EDMX for `V2FeedPackage` plus the three function imports) | `src/FiGet.Protocol.V2/AtomWriter.cs` |
| Atom feed and entry writers with the full property set, dependencies as `id:range:tfm` triples | `src/FiGet.Protocol.V2/AtomWriter.cs` |
| `FindPackagesById()`, `Search()`, `Packages()`, `Packages(Id=,Version=)`, `GetUpdates()`, `/$count` on each | `src/FiGet.Protocol.V2/NuGetV2Endpoints.cs` |
| Hand-written `$filter` parser and `$orderby`, both failing loudly with 400 | `src/FiGet.Protocol.V2/ODataFilter.cs` |
| Push to the feed root and to `package`, delete under the root and under `package` | `src/FiGet.Protocol.V2/NuGetV2Endpoints.cs` |
| Download by normalised version | `src/FiGet.Protocol.V2/NuGetV2Endpoints.cs` |
| The merged version list drives the latest flags, so exactly one entry claims each | `V2Row.ForPackage` over `VersionListBuilder` |

### Evidence

`dotnet test` on the commit that adds this section: 119 total, 90 passed, 29 skipped (the SQL Server half).
The v2 tests drive the request shapes recorded in phase 0, including the source-validation probe, the
PowerShellGet tag search, the PSResourceGet filter dialect with a version range ordered descending, inline
count with a next link, an unsupported filter answered 400, unlist moving the latest flag, and a private
feed challenging for credentials.

### Decisions taken while building

- **Ordering defaults differ per operation.** `FindPackagesById()` returns versions ascending, which is what
  the reference server returned and what keeps client paging stable; `Search()` and `Packages()` default to
  id ascending then version descending. Version properties always compare as NuGet versions.
- **`$top` is capped at 1000.** PSResourceGet asks for 6000; it pages with `$skip` when it receives fewer.
- **The answer keeps the root the client used.** A request under `/api/v2` gets URLs under `/api/v2`, so a
  client that registered the alias never leaves it.
- **A listing without an id scans a bounded window of packages** (2000) before flattening versions, because
  the client pages over entries while the store pages over packages.

### Still to do in phase 2

- Replay the `tests/fixtures` recordings as tests (build plan section 7.1). The fixtures are committed and
  the shapes they carry are covered by hand-written tests, but nothing reads the fixture files yet.
- Run the real Windows PowerShell 5.1 client against this surface (section 7.2). That needs the compat
  scripts pointed at a running instance.

## Architecture: Domain, Application, Infrastructure — 2026-09-12

**State: done and verified. The layering the build plan has described since before the first line of code
is now enforced by a test instead of by habit.**

Section 3 said "No ASP.NET, no EF references" from the start, and the rule mostly held — entities stayed
plain, EF lived in its own project, the host was the only composition root. It did not hold completely:
`NuGetUpstreamClient`, an HTTP client for other people's servers, sat in the same assembly as the
entities. Nothing failed when it landed there, which is the whole problem. A boundary that only exists
in a document is a boundary that drifts.

### What moved

| Was | Is | Why |
|---|---|---|
| `FiGet.Core/Entities`, `Versions`, `Search`, `Feeds`, `Packages/IndexedPackage` | `FiGet.Domain` | Entities and rules that hold regardless of storage or protocol |
| `FiGet.Core/Stores`, `Storage`, `Connectors/IUpstreamClient`, the indexer interface | `FiGet.Application/Ports` | Every dependency on the outside world, stated as an interface |
| `FiGet.Core/Connectors/ConnectorService`, `Packages/PackageIngestionService`, `Tokens` | `FiGet.Application` | What the server does, against those ports |
| `FiGet.Persistence` + `FiGet.Storage` + `NuGetUpstreamClient` + `PackageIndexer` | `FiGet.Infrastructure` | One project for the adapters: EF, disk, upstream HTTP, nupkg reading |
| `FiGet.Persistence.Sqlite` / `.SqlServer` | `FiGet.Infrastructure.Sqlite` / `.SqlServer` | Names had to follow the project they extend; they still hold nothing but migrations |
| `FiGet.Core.Tests` | `FiGet.Unit.Tests` | It never mirrored one assembly, and now mirrors none |

`FiGet.Http`, `FiGet.Protocol.V2`, `FiGet.Protocol.V3` and `FiGet.Web` kept their names. The protocol
projects now reference Domain and Application only, so neither can reach EF even by accident.

### The guard

`tests/FiGet.Unit.Tests/LayerBoundaryTests.cs` asserts on each assembly's **compiled** reference list,
not on the project file alone, because that is the only statement that cannot be argued with: an
assembly lists what it actually binds to. Domain must not reference ASP.NET, EF Core,
`Microsoft.Extensions.*`, `NuGet.Protocol` or `NuGet.Packaging`, and must reference no other FiGet
project. Application must not reference ASP.NET, EF Core, `NuGet.Protocol` or `NuGet.Packaging`, and its
project file must reference Domain and nothing else. Logging **abstractions** are deliberately allowed in
Application: a contract, not an implementation.

### Evidence

Run on the commit that adds this section:

| Check | Result |
|---|---|
| `dotnet build` | 0 errors, 0 warnings |
| `dotnet test` | 149 total, 0 failed, 29 skipped (the SQL Server half) |
| Boundary tests actually ran | 137 tests before, 149 after, 0 failed — the 12 new cases were discovered, not skipped |
| `dotnet ef migrations has-pending-model-changes`, both providers | "No changes have been made to the model since the last migration" |
| `git status` | Every move recorded as a rename, so `git log --follow` still works |

The migration check is the one that mattered most: every entity's CLR name inside both model snapshots
changed from `FiGet.Core.Entities.*` to `FiGet.Domain.Entities.*`, and the model still matches.

### Decisions taken while doing it

- **Domain may reference `NuGet.Versioning`, and nothing else.** Version comparison is the domain, not a
  detail; our own comparer would mean disagreeing with nuget.org about which version is latest, silently.
  Recorded as `docs/decisions/0001` because the plausible-looking next step — "then `NuGet.Packaging` is
  fine too" — is wrong, and the reasoning is not visible from the code.
- **Renaming the migration assemblies is safe for live databases.** `MigrationsAssembly` is derived from
  `typeof(...).Assembly.GetName().Name` rather than a literal, and `__EFMigrationsHistory` stores
  migration ids, not assembly names. Checked before the rename, not after.
- **`docs/decisions/` starts here**, with the same three-part test used elsewhere: the reason is not
  visible from the code, the obvious-looking change is wrong, and rediscovering it costs a day. Anything
  failing one of those is a code comment instead.

### Reading the older sections of this log

Entries above this one name paths under `src/FiGet.Core`, `src/FiGet.Persistence` and `src/FiGet.Storage`.
They were accurate when written and are left alone; the table above translates them.

### Next

Unchanged from "Next, in this order": the design-system port, then the admin area, then the themed
dropdown.

## The design-system port — 2026-09-12

**State: done and verified. Every page is on the design system, and a theme pack now restyles all of it
rather than the handful of colours the old stylesheet defined.**

This was step 1 of "Next, in this order" above. The old `app.css` was 135 lines and ten tokens, which
meant a theme pack could change the background and the accent and nothing else: buttons, tables, badges
and tabs were hard-coded. Ported the full token set and the component classes from the sibling
application, then rewrote all ten pages onto them.

### What changed

| | |
|---|---|
| Tokens | The full set: surfaces (`bg`, `surface`, `inset`, `hover`), borders, three text weights, topbar chrome, an accent triad with its contrast colour, four status colours each with a soft variant, radii, shadows and fonts |
| Components | `fg-` classes for the shell and topbar, cards, panels, tables, toolbars, pagers, buttons, pills, badges, chips, forms, alerts, tabs, breadcrumbs, definition lists and the copyable URL |
| Packs | **YAML**, via YamlDotNet 16.3.0 — the same version the sibling application pins, so a pack moves between them unchanged. `graphite.json` became `graphite.yaml` on the ported token names |
| Pages | All ten rewritten: the shell, feed list, feed packages, package, version, settings, tokens, sign-in, error and not-found |
| Config | `appsettings.json` now lists the `Theming` keys, which existed but were documented only in `docs/configuration.md` |

Dark is stated twice in the base stylesheet and in every compiled pack, deliberately: these pages ship no
script, so the media query answers a reader whose system asks for dark, and the `[data-theme]` attribute
answers an explicit choice and has to win over it. One block cannot do both.

### The source filter is now links, not a dropdown

The proxy-feed source filter was a native `<select>`, the one control a stylesheet cannot theme and the
reason the themed dropdown is a separate step. Under static SSR it is better expressed as pill links
carrying the query: no script, bookmarkable, and it matches the record list it sits above. That removes
one native select from the public pages outright, leaving the remaining ones on admin forms — which is
where step 3 now actually applies.

### One thing the port could not copy

The record list it was modelled on is a **QuickGrid** under `@rendermode InteractiveServer`. These pages
are statically rendered by design, so the component itself is not available: sorting and paging would
need a circuit. What was ported is the appearance and the structure — the dense table with mono uppercase
headers and hover rows, the filter toolbar above it, the pager below — driven by links and form posts
instead. Worth stating plainly, because "same as the dossier list" is true of the look and not of the
machinery. Turning interactivity on for these pages remains an open decision, and it is the same decision
step 3 hangs on.

### Evidence

| Check | Result |
|---|---|
| `dotnet build` | 0 errors, 0 warnings |
| `dotnet test` | 149 total, 0 failed, 29 skipped — unchanged from before the port, so no asserted markup broke |
| Live run, YAML loader | `Theme loaded: graphite from graphite.yaml` |
| `GET /themes/graphite.css` | 200, `text/css`, 1,947 bytes, tokens compiled correctly |
| Compiled pack, dark blocks | Both emitted, braces balanced 4/4, the nested `@media` closing as `} }` |
| `GET /` | 200, carries both stylesheet links and renders `fg-` classes |
| Class sweep | Every `class="..."` in every razor file is an `fg-` class or a defined state class |

Trap met while verifying: `dotnet run` ignores `ASPNETCORE_HTTP_PORTS` when `launchSettings.json` exists
and binds to the profile's URL instead. Pass `--no-launch-profile`. Backgrounding an `&&` chain with a
trailing `&` also backgrounds the variable assignments in it, and killing that returns the subshell's
id, not the server's — which leaves a `FiGet.Web` holding the build lock.

### Next

1. **An admin area with its own side menu.** Unchanged, and the stylesheet for it is already in place:
   `fg-admin-shell`, `fg-admin-nav` and friends were ported with the rest, so that step is markup and
   routing rather than design.
2. **The themed dropdown**, which now matters only for the admin forms, and still needs the decision
   about whether those pages become interactive.

## A theme that loaded nowhere, and interactivity decided — 2026-09-12

### An empty path is not an unset path

Deploying the design-system port exposed a defect introduced by the port itself. `ThemeService` resolved
its directory with `configuration["FiGet:Theming:Path"] ?? Path.Combine(webRoot, "themes")`, and `??`
falls back only on **null**. Adding `"Theming": { "Theme": "", "Path": "" }` to `appsettings.json` — so
the keys are discoverable, which is how every other key in that file is written — made the empty string
win. The themes directory became `""`, no pack loaded, and `/themes/graphite.css` answered 404 while the
page went on linking it. The site rendered, on built-in defaults, looking almost right.

Every local run had passed because the configuration block was added *after* the run that proved the
loader worked. The suite could not have caught it either: the loader had no tests at all.

Fixed by treating empty or whitespace as unset, which is what an empty default means everywhere else in
that file, and covered by seven new tests in `FiGet.Integration.Tests` — the first this loader has had.
Three of them are the regression itself (`""`, `"   "`, and the key absent), and the rest cover a pack
compiling both dark selectors with balanced braces, a missing directory being no error, and one broken
pack being skipped without taking the others down.

| Check | Result |
|---|---|
| `dotnet test` | 156 total, 0 failed, 29 skipped (149 before, so all seven ran) |
| Live `GET /themes/graphite.css` | 200, `text/css`, 1,868 bytes, accent `#9a6400` present — was 404 |
| Container log | `Theme loaded: graphite from graphite.yaml.` — was `No themes directory at ` |
| Error lines in the container log | 0 |

The lesson is the same shape as the v2 empty-feed bug recorded above: **the failure was in the path that
runs when a value is absent**, and absence is exactly what a local run with the key missing does not
exercise. Worth stating once more because it has now happened twice on this project in two days.

### Admin pages become interactive; public pages stay static

Decided with the user. The one real objection to Blazor circuits was session affinity across replicas,
and the load balancer in the target environment provides it, so that objection does not apply here.

- **Admin pages** get interactive rendering: the grid with live sorting and filtering, and the themed
  dropdown as the real component rather than something rebuilt by hand. That is most of step 3 removed
  rather than solved.
- **Public pages stay static SSR** and keep shipping no script, which is why the proxy-feed source filter
  stayed as pill links rather than becoming a dropdown again.
- **Residual cost that affinity does not remove:** a circuit still drops on a pod restart or a rolling
  deploy, so an interactive page needs a reconnect overlay. The sibling application already has one, so
  it ports across rather than being new work. Nothing else about the deployment changes: the public
  surface, which is what the NuGet and PowerShell clients actually use, stays scriptless and stateless.

### Next

The admin area, now with interactive rendering available for it, followed by the themed dropdown as part
of that rather than as a separate rebuild.

## Review feedback, and an interactive grid for signed-in readers — 2026-09-12

Everything here came from using the deployed instance, which is why it is worth writing down separately
from the port that preceded it: none of it was visible while building.

### What the review found

- **The source filter moved into the search bar** as a dropdown, rather than a row of pills beneath it.
  It auto-submits, so choosing stays one action. A native select's popup list is drawn by the operating
  system and cannot be fully themed; everything around it is, and it sits inside the bar so the seam
  does not show.
- **A package's tabs now match a version's** — Overview, Metadata, Dependencies, Files, plus All
  versions. That also gets the raw tag dump off the overview: on a module publishing one tag per
  exported command it ran to thousands of characters and buried the four tags anybody reads. It is on
  the Metadata tab as chips, folded, exactly as the version page already did it.
- **Overview keeps Version, Authors and Published** on both levels.
- **The package icon is shown.** It was stored and never rendered. It comes from whoever published the
  package and points somewhere we do not control, so a failed load removes the element rather than
  leaving a broken-image box — an air-gapped install would otherwise show one on every row.
- **A dark and light toggle.** The stylesheet already answered the system preference; this records an
  explicit choice, which has to beat it. The attribute is applied by an inline script in `<head>`,
  before first paint, or every navigation would flash the other theme first.

### Two defects it also found

- **Upstream-only packages were not links.** The feed list rendered the id as plain text unless the
  package was held locally, while `/feeds/{feed}/packages/{id}` worked perfectly well for one that is
  only upstream. A colleague found it by reaching the page by hand and noticing the search would not
  take him there — so the single case a proxy feed exists for was the one case that could not be
  clicked.
- **`display: flex` on a `<td>`.** It overrides `display: table-cell`, which takes the cell out of the
  row: the actions column became a wide empty gutter, the row borders stopped short of it, and the
  downloads column was pushed off the edge. Fixed by styling the cell as a cell and shrinking it to its
  content.

### Interactive rendering, for signed-in readers only

Decided with the user. Signed-in readers get a QuickGrid that searches and pages without reloading;
anonymous readers keep the static table. The split is deliberate: the public view is reachable without
credentials, and a circuit is server state held for as long as somebody keeps the page open, so it is
not somewhere to allocate it. `blazor.web.js` is therefore emitted only when signed in — the read-only
view still downloads no framework at all.

Three consequences worth recording, because none is obvious from the markup:

- **There is no `HttpContext` in a circuit**, so anything derived from the request is passed in by the
  statically rendered page above.
- **Scoped services live as long as the circuit**, so resolving `IPackageStore` directly would hold one
  DbContext open per connected reader. Every query takes its own scope through `IServiceScopeFactory`
  and disposes it. The sibling application solved the same problem with `AddDbContextFactory` and warns
  in a comment that registering that *and* `AddDbContext` cost real money; taking a scope needs no
  second registration at all.
- **Pull became a circuit call rather than a form post**, because there is no `HttpContext` to mint an
  antiforgery token. The static table lost its admin branch entirely: signed-in readers get the grid, so
  that code was unreachable.

`ReconnectModal` ports across with it. Session affinity keeps a circuit on one replica, but a pod
restart or a rolling deploy still drops it, and without the dialog the page looks alive and ignores
every click.

**What this does not do: sortable columns.** QuickGrid sorts for free only when given an `IQueryable`.
This grid is fed by `IPackageStore.SearchAsync`, a port with skip, take and a filter but no ordering,
and pointing the grid at EF directly would put queries in the composition root and undo the layering.
The honest fix is a sort parameter on the port plus both EF implementations; it is in `docs/backlog.md`.

### The guard that caught itself being useless

Three tests now assert the split: the anonymous view ships no framework and no prerender marker, signing
in brings both, and the reconnect dialog exists only where a circuit does.

The first run failed two of them, and both failures were worth having. One was a flaky assertion of mine
(whether a feed holds packages depends on what other tests in the shared fixture have pushed). The other
was real: the cascading `HttpContext` is **not** populated on the root component, though it reaches the
pages inside `Routes` — so the check answered "anonymous" for everybody and the framework would never
have shipped in production. `AuthorizeView` is the signal that works that far up.

Then the fix exposed a third problem. `MapStaticAssets` fingerprints served names, so the script is
requested as `blazor.web.<hash>.js` and the literal `blazor.web.js` appears nowhere. The positive
assertion failed loudly — but the negative one had been **passing for the wrong reason** and would have
gone on passing no matter what the page contained. A test that cannot fail is worse than no test, and
this repo has now been bitten by that fingerprinting twice.

| Check | Result |
|---|---|
| `dotnet build` | 0 errors, 0 warnings |
| `dotnet test` | 159 total, 0 failed, 29 skipped (156 before) |

### Also

`docs/backlog.md` now exists, holding what is not built and why — including a role above admin, raised
2026-09-12. Recorded with the observation that the obvious form of it restricts nothing: an admin who
can create tokens can create an *admin* token and use that, so "admins cannot delete tokens" only means
something if nobody may mint a token carrying more than they hold.

## A silent 404 that killed every interactive component — 2026-09-12

**A colleague reported "search is broken when logged in". It was, and nothing in the logs said so.**

The signed-in feed view renders an interactive grid. The page prerendered correctly — the table drew,
the pager even reported "5 items Page 1 of 1" — and then ignored every keystroke, because the circuit
never started. The circuit never started because the page asked for `_framework/blazor.web.js` and got a
404. Nothing threw, so there was no error to find: the container log had six lines and none of them were
about this.

### Cause: `--no-restore` on the publish

`deploy/Dockerfile` published with `--no-restore`, which skips the work that composes static web assets.
`wwwroot/_framework/blazor.web.js` was therefore never written into the image, while the asset manifest
still advertised it — the page emitted a correct fingerprinted URL for a file that did not exist.

Proven by building the same source twice on the same host with the same SDK, changing only the flag:

| Image | Publish | `wwwroot/_framework` | blazor entries in manifest |
|---|---|---|---|
| `figet:diag` | `--no-restore`, and `--no-cache` to rule out layer reuse | **absent** | **0** |
| `figet:dev` | without the flag | `blazor.web.js` | present |

Layer caching was the first suspicion and was wrong: a completely fresh build still omitted the assets.
The SDK was the second and was also wrong — the build image and this machine both resolve to 10.0.401
under our `global.json`. The sibling application's Dockerfile already carried both the fix and a comment
naming `--no-restore` as the cause; this repository had the flag and neither.

**The publish now runs without it, and a build-time check fails loudly if the script is missing**, dumping
`wwwroot` so the log shows what was emitted instead. The guard lives in the Dockerfile rather than in CI,
so every path that builds the image inherits it.

### Three more defects the same screenshot showed

- **The scoped stylesheet was never linked.** `App.razor` did not reference `FiGet.Web.styles.css`. A
  project with no `.razor.css` files does not need it and nothing complains when it is missing, so adding
  component CSS silently shipped framework components unstyled — the grid's pager rendered as blank stubs.
- **Forty-five empty rows.** Still open, and the first two explanations here were wrong — see the
  correction below. The grid does pad each page to its page size, but not with anything a stylesheet can
  select.
- **"0 packages" above a full table.** The items provider runs *during* a render, so assigning the count
  never repainted the line showing it. Queued with `InvokeAsync(StateHasChanged)`.

### Verified live

| Check | Before | After |
|---|---|---|
| `GET /_framework/blazor.web.js` | 404 | **200, 200,645 bytes** |
| Scoped bundle on the page | not linked | linked, resolves 200 |
| Framework shipped to anonymous readers | n/a | still 0 — the split holds |
| Error lines in the container log | 0 (nothing threw) | 0 |

### What the restart also revealed: the describe pass costs seconds

Recreating the container emptied the in-memory upstream metadata cache, which finally allowed a genuinely
cold measurement of a large package:

| Package | Versions | Cold | Warm |
|---|---|---|---|
| AdminByRequest | 7 | 0.57s | — |
| Az.Accounts, Pester | 119–144 | ~1.0s | 0.13s |
| PnP.PowerShell | 2098 | **22.99s** | 0.23s |

Twenty-three seconds to render a page that shows ten rows. Attributed here, from reading the code, to the
describe pass: `UpstreamCandidatesAsync` fills in description, authors and tags for all 2098 versions
before the page selects the handful it displays. **That attribution was half right, and the half it got
wrong was the half that mattered** — see "The 23-second page was two walks, not one describe" below.
With a five-minute TTL this is the normal path, not a cold-start curiosity.

### Two traps met while diagnosing, both mine

- **`docker run --rm image sh -c '…'` does not run a shell** when the image has an `ENTRYPOINT`: it passes
  the arguments to the entrypoint. Here that started the web server, which never exits, so the command hung
  and its ssh session buffered forever — producing a zero-byte log that looked like a wedged machine. Use
  `--entrypoint sh`. Two commands were lost to this before the process list showed
  `dotnet FiGet.Web.dll sh -c ls …` and gave it away.
- **Asserting on a fingerprinted filename cannot fail.** Covered above on its own; it recurred here because
  the served name is `blazor.web.<hash>.js` and the literal never appears.

## Correction, and where the grid's empty rows actually come from — 2026-09-12

Two explanations recorded above for the grid's blank rows were wrong, and a third guess was worse. The
record is corrected here rather than edited away, because the wrong answers are the useful part.

- **Wrong:** "the grid pads with `aria-hidden` placeholder rows", copied from the sibling application's
  `::deep tr[aria-hidden="true"]`. That rule matched nothing here and hid nothing.
- **Wrong:** hide any row whose cells are all empty (`tr:not(:has(td:not(:empty)))`). That treats the
  symptom and would hide a genuine row whose columns happen to render nothing.
- **Also wrong, and worth admitting:** the rows are *not* QuickGrid's placeholder rows at all. Those exist
  only under `Virtualize`, and they carry `grid-cell-placeholder` and an `aria-rowindex`.

**What actually happens**, from `QuickGrid.razor` on `release/10.0`: `RenderNonVirtualizedRows` renders the
real rows, and then

```csharp
// When pagination is enabled, by default ensure we render the exact number of expected rows per page,
// even if there aren't enough data items. This avoids the layout jumping on the last page.
// Consider making this optional.
if (Pagination is not null)
{
    while (rowIndex++ < initialRowIndex + Pagination.ItemsPerPage)
    {
        <tr>@foreach (var col in _columns) { <td class="@ColumnClass(col)" @key="@col"></td> }</tr>
    }
}
```

So it pads to `ItemsPerPage` **only when `Pagination` is set**, which is why the published samples never
show it, and it marks those rows with nothing whatsoever — no class, no `aria-rowindex`, the same `<tr>`
and `<td class="col-justify-start">` as a real row. There is no handle for CSS, by construction. The
framework's own comment still says "Consider making this optional".

`ItemsPerPage` is 50 and the feed holds 5, so forty-five blank rows are the documented behaviour rather
than a defect. The fix is therefore not a stylesheet rule at all.

### What to do next

One change to `Components/Shared/PackageGrid.razor`, taking over both jobs QuickGrid is doing badly here:

1. **Stop passing `Pagination`** to the grid and page the items provider directly, which removes the
   padding at its cause. (Setting a smaller `ItemsPerPage` only makes the blank rows fewer.)
2. **Replace `<Paginator>`** with a pager built from `fg-btn`, matching the anonymous view's pager. This is
   wanted independently: `Paginator`'s scoped rules are `.paginator[b-3qssc0bm46]`, so they outrank plain
   selectors on specificity, they hard-code `border-top: 1px solid #ccc` which ignores the theme, and the
   arrows come from `background: none center …` on the button — which the current
   `.fg-pager .paginator button` block overwrites, producing the blank circles now on the live site. Remove
   that block with it. Model the pager on the sibling application's `GridPager`, which passes the count in
   rather than reading `PaginationState.TotalItemCount` (not populated on every path).
3. `wwwroot/app.css` currently carries a comment where a rule used to be, explaining why there is none.

**Then take it to the other two applications.** The sibling grid is on the same component with `Pagination`
set, so it pads the same way; its `::deep tr[aria-hidden="true"]` rule suggests somebody hit this and
settled for hiding something that was never there. Worth an entry in both backlogs once the fix here is
proven, along with the `--no-restore` publish trap, which is a far more serious silent failure than this
one.

### Done, 2026-09-12 (commit `437fdde`)

The fix above is applied and deployed. The provider now applies its own offset and no `PaginationState`
reaches the grid, so the padding loop never runs; the pager is the same `fg-btn` one the anonymous view
uses, and the `.fg-pager .paginator` block is gone.

Verified against a published build, signed in: **1 `<tr>`** where there were 51, **0** empty padding rows,
**0** `.paginator` elements, and `/_framework/blazor.web.js` still 200. CI green on both jobs; the running
image matches `figet:dev`; zero error lines.

**Not verified:** the replacement pager's own rendering. It is behind `@if (total > PageSize)` and no feed
here holds more than fifty packages — upstream searches cap at fifty too — so the branch never ran. The
markup and styling are the same ones the anonymous version list already renders with two thousand
versions; what is untested is the `@onclick` wiring into `GoToAsync`.

## The 23-second page was two walks, not one describe — 2026-09-12

The backlog's fix was to describe lazily: stop describing inside `UpstreamCandidatesAsync` and let each
caller ask for the rows it renders. Before building it, the premise was measured against the real galleries
with a throwaway probe instead of inferred from the code. It did not survive.

Per call, cold; `pnp.powershell` on the PowerShell Gallery (v2), `awssdk.core` on nuget.org (v3):

| Call | v2, 2098 versions | v2, 7 versions | v3, 1504 versions |
|---|---|---|---|
| `GetAllVersionsAsync` (versions only) | 13.4s | 0.22s | **0.17s** |
| `GetMetadataAsync`, unlisted excluded (the describe) | 8.5s | 0.17s | 3.7s |
| **both, as the connector did** | **21.9s** | 0.39s | 3.9s |
| `GetMetadataAsync`, unlisted included | **8.4s** | 0.15s | 3.2s |
| per-version describe, 11 rows, in parallel | 0.33s | 0.31s | 0.66s |

Three things follow, and only the first was in the backlog:

1. **The version list is not the cheap half.** On a v2 gallery `GetAllVersionsAsync` alone is 13.4 of the
   21.9 seconds. Describing lazily would have removed the 8.5s and left the 13.4s, so the page would still
   have taken thirteen seconds and the agreed fix would have looked like a failure.
2. **On v2 both calls are the same walk.** `RemoteV2FindPackageByIdResource` and
   `PackageMetadataResourceV2Feed` both page through `FindPackagesById()`; v2 has no versions-only endpoint
   to be cheap about. Asking separately paid for that walk twice. One walk with `includeUnlisted: true`
   returns all 2098 versions *with* their descriptions, misses none, and costs 8.4s — less than either half
   cost on its own.
3. **On v3 the opposite holds.** The flat container answers versions in 0.17s while the registration walk
   costs 3.2s, so there they really are different endpoints and describing lazily is worth about 19x.

The fix is therefore protocol-shaped, which is why it lives in the adapter and not in the workflow: one
walk on v2, and on v3 the version list still comes from the resource that decides what can be downloaded.

### What changed

`IUpstreamClient.GetVersionsAsync` and `GetMetadataAsync` became one `GetCatalogAsync` returning an
`UpstreamCatalog` of versions and descriptions, cached together. They have to be cached together: the
version list lives in the database and the descriptions in memory, so after a restart the list is fresh and
the descriptions are gone, and taking only the first would serve a listing of blank rows.

Measured effect on the upstream calls a cold `PnP.PowerShell` page makes: **21.9s to 8.4s**. The remaining
8.4s is the walk itself and cannot be made smaller from this side; what removes it from the user's path is
caching it for longer than five minutes and refreshing behind the request, which is now the backlog item.

Guarded by `An_upstream_listing_is_described_without_a_second_call`: the stub upstream counts catalogue
fetches, the listing shows the description, and the count is one. Counting is the only way to see this from
inside a test, because both shapes produce identical output.

### Two findings recorded rather than acted on (the first was acted on the same day)

- **2062 of PnP.PowerShell's 2098 versions are unlisted upstream.** The gallery advertises 36. FiGet marks
  every upstream candidate `Listed: true`, so it shows all 2098 — about sixty times what `Find-Module`
  would. Left as a decision here; taken the same day, below.
- **Some callers need no descriptions at all.** `/v3/flatcontainer/{id}/index.json` returns a bare version
  array, and a registration index above 128 versions inlines no leaves. On v2 that saves nothing, because
  the walk is the cost either way; on v3 it is the 0.17s-versus-3.2s difference.

## Unlisted upstream versions are now unlisted here — 2026-09-12

Decided the same day it was found: honour the upstream's flag.

The defect was one hardcoded `Listed: true` on every upstream candidate in `ConnectorService`. Nothing
downstream of it was wrong. `VersionListBuilder` already skips unlisted versions when computing the latest
flags, v3 search and autocomplete already filter on `Listed`, a registration leaf already emits `listed`
with the 1900 sentinel published date, and `V2Row` already exposes it as an OData property. One line was
lying to all of them.

What it cost, concretely: the gallery advertises 36 versions of PnP.PowerShell and withdraws the rest, so
**the newest withdrawn nightly was being offered as the latest version of the module**. That is not a
cosmetic row count — `Install-Module` with no version takes the latest, so it was installing a version the
gallery had deliberately hidden.

**Unlisted is not absent, and the fix must not conflate the two.** An unlisted version stays downloadable
by exact version, because a pinned dependency asks for one and does not care whether the gallery still
advertises it — the flat container serves unlisted versions for exactly this reason. Filtering the version
out of the list would have passed a "not the latest" test while breaking every pinned install, so the
guard asserts both halves: `An_unlisted_upstream_version_is_not_latest_and_still_downloads`.

Truncating the list instead — to the 128 versions a v3 registration inlines, say — was considered and
rejected before it was built. It breaks those same pinned installs, and it breaks the merged-latest rule
this server exists to get right, which is computed across the whole list and not across a window of it.

No migration was needed. The flag rides on `UpstreamMetadata` rather than on the cached version list,
which is stored in the database as bare space-separated strings. That works because a catalogue is only
ever answered from the cache when both halves are present, so a version carrying a description also
carries its flag; a version an upstream reports without describing defaults to listed, which is the safe
answer.

### What the tables show

Honouring the flag turned a hidden problem into a visible one. The package page rendered every version it
knew about, so PnP.PowerShell would have shown 2098 rows with 2062 greyed out, and the overview's "Recent
versions" — the newest ten *by version* — would have been ten withdrawn nightlies sitting under a header
that correctly named the current release.

So the tables mirror the gallery: an unlisted version is not shown at all. PSGallery's own page for
PnP.PowerShell lists 36 versions and withdraws the other 2062, and that is now exactly what this page
lists. The merged list stays whole behind it, because the latest is computed across all of it — the hiding
is display only. A line states how many are hidden and that they stay installable by exact version.

The rule took two corrections to reach, both recorded because both were wrong in an instructive way:

1. **"An admin sees everything."** Role-based visibility makes an admin's page disagree with what every
   client sees, exactly when the question is "why did `Install-Module` pick that version" — and it still
   hands 2098 rows to the one person who needs the page readable.
2. **"Everything this feed holds, plus whatever is still advertised."** Defensible, but still not what the
   gallery shows, and the argument for it — that an admin must be able to relist or delete a held copy —
   turned out not to need this table at all. `POST /v3/publish/{id}/{version}` relists, and local search
   and autocomplete had been filtering unlisted versions out all along, so the version table was the sole
   exception rather than the rule.

What the hiding must never touch, and does not: **resolving an exact version.** A pinned install names its
version and does not care whether the gallery still advertises it — the fleet's Ansible baseline pins
versions, so that is the normal case and not a corner. The flat container still serves every stored
version, and `/nuget/{feed}/package/{id}/{version}` still downloads one. Hiding is a listing decision;
fetching is a different path and was never filtered.

The gap this leaves is deliberate: a version this feed holds but has unlisted is now invisible in the web
UI, and relisting it is an API call. That belongs to the admin area, which is next — backlogged rather
than patched over here.

Deliberately untouched: `Find-Module`, v3 search and autocomplete, and the flat container. Search was
already filtering on the flag, and the flat container already serves unlisted versions on purpose.

### What the reference server does, measured

Asked directly rather than assumed, against the reference server on the NAS, whose `modules` feed
is connector-backed:

```
GET /nuget/modules/FindPackagesById()?id='PnP.PowerShell'&$inlinecount=allpages
<m:count>2098</m:count>
```

**2098 — the same number FiGet returned before the fix.** The reference server does not honour the upstream's listed
flag either, so what looked like FiGet misbehaving was FiGet matching the reference server. Honouring the flag is
therefore a step past the server being replaced, not a deviation from it, and parity is the wrong target
here.

One more thing fell out of the same session, unprompted: the reference server's `Search()` on that feed returns **no
results** for `pnp.powershell`, while its `FindPackagesById()` returns all 2098 versions of it. Its
connector can serve the package but its search cannot find it — the failure mode recorded in build plan
section 4.3 as the reason not to guess the OData subset. FiGet's search finds it, verified live against
both the v2 API and the anonymous UI.

## Deployed, and the measurement I got wrong — 2026-09-12

Built on the NAS from `git archive HEAD` (f5046f1), image `sha256:0f1ebc2f`, container recreated onto it.
The Dockerfile's guard passed, so the framework script is in the image.

| | before (two walks) | after (one walk) |
|---|---|---|
| cold PnP versions tab | 21.72s | **13.12s, 15.28s, 15.10s** |
| warm | — | 0.66s, 0.73s |
| versions the page lists | 2098 | **37** |
| `3.4.9-nightly` Listed | true | **false** |

**The prediction was 8-9s cold, and it was wrong: the honest figure is 13-15s.** The error is worth naming
because it was avoidable. The probe had measured that same upstream walk three times — 8.4s, 10.1s, 12.2s —
and the best of the spread was quoted as though it were the number. A range was known and a point estimate
was given from its optimistic end.

What does hold up: the 21.72s baseline matches the two-walk model (13.4 + 8.5 = 21.9s) to within one
percent, and the deployed cold times sit where one walk plus page overhead should. The improvement is
about a third, not the sixty percent the prediction implied.

The weak spot, stated rather than buried: **the before figure is one sample and the after is three.** A
controlled A/B would settle it — the previous image is still on the host as `537ec428aae1` — and has not
been run.

Also confirmed live: `/health/ready` and `/health/live` both answer 200 on the container. Publicly they
404, so Traefik does not route `/health` — harmless, since CI checks the container directly, but it means
there is no external readiness probe.

### The 37th version, and the rule it half-applies

The gallery advertises 36 versions; the page shows 37. The rows say why:

```
35 Upstream + 2 Cached  (1.11.0, 1.9.61-nightly)
```

`1.11.0` is one of the advertised 36 and is held here, so it merges and shows as Cached. `1.9.61-nightly`
is **not** advertised: a cached copy of a version the gallery has since unlisted, whose local row still
says listed. Hence 37 rows, and hence zero unlisted badges — that row is not unlisted at all.

`ReconcileWithdrawnAsync` unlists a cached copy that has been **withdrawn**, meaning absent upstream, but
not one that is merely **unlisted** upstream. Both mean "stop offering this", so the rule is half applied.
Backlogged rather than fixed in the same breath: it changes what a cached copy does, and there is a real
argument on the other side.

## A chosen theme lasted exactly one page — 2026-09-12

Reported from using the deployed instance: the light/dark toggle switches, the choice is gone on the next
page, and the button's glyph never changes. Two faults, one of them deliberate.

**The glyph never changed because it was never meant to.** `MainLayout.razor` ships a literal `◐` under a
comment saying the button is stateless, which was true when static SSR was the only renderer — the server
cannot know what the reader chose. The answer is not to render it server-side but to paint it in script,
where the choice already lives.

**The choice was dropped by enhanced navigation.** It is stored in `localStorage` and applied by an inline
`<head>` script before first paint. For a signed-in reader the framework is loaded, so following a link is
a DOM patch rather than a page load: that script never runs again, and the attribute it set does not
survive the patch. An anonymous reader gets no framework at all and never saw it. So it reads as "broken
when signed in" — the same shape as the earlier circuit fault, and again not the same cause.

Fixed by re-applying the stored choice on Blazor's `enhancedload` event and painting every toggle from the
same function, so the glyph and the attribute cannot disagree.

Four guesses died on the evidence, each worth recording:

- **The theme pack looked unguarded**, which would have made the CSS the culprit. It is not:
  `graphite.css` emits `@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) {`, exactly
  the guarded form. The grep that said otherwise matched only as far as the first brace and hid the inner
  selector — the fourth miscount from a partial grep in one session.
- **A Content-Security-Policy blocking inline script** would explain it precisely. The only CSP present is
  `frame-ancestors 'self'`, which does not restrict scripts.
- **The inline restore script might be missing** from the delivered HTML. It is there.
- **The deployed `app.js` might be stale** and lack the write. It serves 200 and contains it.

**Not verified in a browser**, because there is none here; the diagnosis rests on reading rather than
observation. It is falsifiable: a reload should keep the choice, following a link should now keep it too,
and an anonymous reader was never affected. If a reload also loses the choice, this is wrong.

## The catalogue outlives the request now, and the 101 MB that came with it — 2026-09-12

The top backlog item: stop refetching a stale catalogue in front of the reader. Measured on the live
instance, `PnP.PowerShell`, the versions tab:

| | before | after |
|---|---|---|
| first view after a container restart | 15.02s | **0.40s** |
| view once the refresh has landed | 15.02s | **0.65s** |
| recurring cost every five minutes | 13-15s | none |

Anything cached is served at once, whatever its age; a catalogue past the window is queued for a refresh
the request does not wait for; only a package nothing is known about still blocks, which is its first view
and never again. `UpstreamIndexTtl` keeps its name, key and default and now means "refresh after this"
rather than "expire after this".

The queue is a channel with an in-flight set, so eight readers of the same stale package cause one walk,
and the worker takes its own scope because the request that asked for it is long gone.

### And then it took the page down

The same change persisted the descriptions alongside the version list, so they would survive a restart.
That shipped, and `PnP.PowerShell` began answering **500**: `OutOfMemoryException` in
`ParseDescribed`, deserialising the column it had just written, inside a container capped at 1 GB.

The numbers say why, and they were measured only after the fact:

```
pnp.powershell   versions: 31,894 chars    metadata: 101,305,359 chars
dbatools                                   metadata:  30,579,150
52 rows                                    metadata total: 141 MB
```

**The estimate written into the commit that shipped it was "somewhere around a megabyte". It was 101 MB,
about a hundred times out.** A PowerShell gallery writes one `PSCommand_*` tag per exported command, on
every version; PnP exports hundreds, and there are 2098 versions. The version list is the cheap half by
three orders of magnitude.

Holding those objects was never the problem - the in-memory cache had done it for weeks. The round trip
was: the column text, the serialiser's rented buffer and the object graph all live at once.

### What was done

Rolled the deployment back to the previous commit first, which restored service at the old speed (15.02s,
correct) while the fix was written. The migration had been purely additive, so the older code simply
ignored the extra column.

Then: the in-memory cache restored, the column dropped, and stale-while-revalidate kept - because it was
always the *version list* that blocked the request, and that is 31 KB. After a restart the descriptions
are gone and rows list plainly until the refresh lands behind the page. Verified live: the first view
shows 2098 undescribed versions in 0.40s, the next shows the 37 the gallery advertises in 0.65s.

### The part worth keeping

**CI passed the commit that broke production**, green in 2m15s, because every catalogue test used one to
three versions. A hundred-megabyte round trip is invisible at that scale. The suite now stands a package
up at a realistic weight - four hundred versions carrying twenty kilobytes of tags each - and exercises
the cached path, which is the one that threw.

### Reclaiming the space, and a second misread

Dropping the column left the file at 142 MB, and a `VACUUM` reported success without changing a byte. The
first reading - "the vacuum did not work" - was wrong, and the pragmas said so: `page_count` 127 at
`page_size` 4096 is **508 KB of actual content, with zero free pages**. The vacuum had worked. The file
was 141 MB of untruncated tail, because in WAL mode SQLite cannot shrink the main file while a connection
holds it open, and the application had one.

Stopping the container, vacuuming through `journal_mode=DELETE`, and starting it again took the file from
142,032,896 to **520,192 bytes** - exactly the 127 pages reported. Twice in one episode the number on the
outside disagreed with the number inside, and both times the pragmas settled it faster than reasoning did.

## Save-Module fails through the proxy, and it is not this server — 2026-09-12

Reported while testing. `Save-Module -Repository <figet-v2>` fails on every package:

```
AdminByRequest  End of Central Directory record could not be found.
Pester          Number of entries expected in End Of Central Directory does not correspond ...
```

Both are zip errors, and the file is not corrupt: fetched with curl it is a valid 25-entry nupkg, and the
bytes served over v2 and v3 are identical. What differs is the wire.

| | Content-Length | Content-Encoding | Transfer-Encoding |
|---|---|---|---|
| the application, asked directly on :8080 | 24977 | none, even when gzip is offered | none |
| the same request through the proxy | absent | **gzip** | chunked |
| PowerShell Gallery, which the same client saves from successfully | 24977 | none | none |

So the proxy is compressing `application/zip` and dropping the length while it does. The NuGet provider
behind `Save-Module` - 3.0.0.1, under PowerShellGet 2.2.5 and PackageManagement 1.4.8.1, which is the
fleet's pinned stack - writes the gzip stream to disk as if it were the package. `Save-PSResource` over v3
succeeds against the very same compressed response, because a modern `HttpClient` decompresses it: this is
a client difference, not a route difference.

**Not caused by anything here.** There is no response compression in this repository, the application sets
a correct `Content-Length` through `Results.Stream(..., enableRangeProcessing: true)`, and it ignores
`Accept-Encoding` entirely. It predates today's work and would have been failing the whole time.

The remedy is to stop compressing already-compressed media at the proxy - exclude `application/zip` and
`application/octet-stream` from the compress middleware, or drop compression for this router. It buys
almost nothing anyway: 24,977 bytes became 23,236, under seven percent, in exchange for breaking the exact
client this server exists to serve.

Worth recording about the diagnosis itself: the first two explanations were wrong and cheap to believe.
"The restarts caused it" fitted the timing and died when the failure reproduced on a warm instance. "The
old client cannot do gzip" fitted the symptom and died when that same client saved the same package from
the gallery, which also serves gzip. Only asking the application directly, without the proxy in front,
separated what this server sends from what reaches the client.
