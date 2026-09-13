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

**Which proxy, settled by test.** Cloudflare was set to DNS-only for the hostname: it then resolved
straight to the origin, the response carried no `Server: cloudflare` and no `CF-RAY` - and
`Content-Encoding: gzip` was still there, with `Save-Module` still failing. Cloudflare was never the
culprit. The middleware is Traefik's, and it is two lines:

```
rules/middlewares-compress.yaml:12    compress: {}        <- no exclusions, so everything
rules/chain-no-auth.yml:8               - middlewares-compress
```

`compress: {}` takes no options, so it compresses every content type, and `chain-no-auth` is the chain
this host uses. The remedy is to stop compressing already-compressed media there - exclude
`application/zip` and `application/octet-stream`, or drop compression for this router. It buys
almost nothing anyway: 24,977 bytes became 23,236, under seven percent, in exchange for breaking the exact
client this server exists to serve.

Worth recording about the diagnosis itself: the first two explanations were wrong and cheap to believe.
"The restarts caused it" fitted the timing and died when the failure reproduced on a warm instance. "The
old client cannot do gzip" fitted the symptom and died when that same client saved the same package from
the gallery, which also serves gzip. Only asking the application directly, without the proxy in front,
separated what this server sends from what reaches the client.

## A proxied package with many versions could not be found — 2026-09-12

Reported while testing: `Find-PSResource -Name dbatools` answered "could not be found in repository",
while `Microsoft.Graph` resolved in milliseconds. Both are proxied from the same gallery.

The difference is the version count. Above `MaxInlinedLeaves` (128) a registration index stops embedding
its leaves and advertises page URLs instead:

```
dbatools         16 pages, 0 inlined  -> every page URL 404
pnp.powershell   33 pages, 0 inlined  -> every page URL 404
microsoft.graph   2 pages, 2 inlined  -> never fetches a page, worked all along
```

`RegistrationIndexAsync` pages the **merged** list, local and upstream together.
`RegistrationPageAsync` did not take a `ConnectorService` at all and chunked
`BuildLocal(package.Versions)` - what this feed holds. On a proxy feed those are different lists, so the
index advertised ranges the page endpoint had never heard of. dbatools is cached here at one version; the
advertised range `0.7.9.7/0.8.709` matched no local chunk, and 404 became "this package does not exist".

`RegistrationLeafAsync` and `CatalogEntryAsync` had the same shape and the same fault. The catalog entry is
the quiet one: its own comment already recorded that PackageManagement's NuGet 3.x provider resolves a
version by following that URL, so a 404 there makes a version silently unavailable rather than visibly
missing. All three now build from the merged list.

### Why the suite did not catch it

`More_than_128_versions_page_the_registration_like_nuget_org` covers paging, and passes: it pushes 130
versions to a **curated** feed, where local versions are the whole truth and the index and the page cannot
disagree. The defect only exists where the two lists differ, which is every proxy feed. The new test pages
a proxy feed and fetches every range the index advertises, plus a leaf and a catalog entry for a version
nobody has cached.

### Verified with the client that reported it

| | before | after |
|---|---|---|
| `Find-PSResource dbatools` | not found | **2.8.4** |
| `Find-PSResource PnP.PowerShell` | not found | **3.4.1** |
| `Find-PSResource Microsoft.Graph` | 2.39.0 | 2.39.0, unchanged |
| `Find-PSResource dbatools -Version 2.1.0` | not found | resolves |

The last one matters most: an exact older version of a paged package reaches it through a page *and* a
catalog entry, which is the path an install walks.

## The proxy stopped compressing packages — 2026-09-12

Applied on the host, with the owner's go-ahead. `middlewares-compress.yaml` said `compress: {}`, which
takes no options and so compresses every content type, and `chain-no-auth` pulls it in for every router on
that chain. It now excludes `application/zip` and `application/octet-stream`.

Verified: a package download returns `Content-Length: 24977` and no `Content-Encoding`, while
`/v3/index.json` still comes back gzipped - so the other services on that chain keep their bandwidth
saving. Traefik reloaded the dynamic file without an error, and the original is kept beside it as
`middlewares-compress.yaml.bak-20260912`.

Worth stating what this was not: nothing in this repository ever compressed anything, the application sets
a correct `Content-Length`, and it ignores `Accept-Encoding`. The failure only existed between a proxy
that gzipped an already-compressed payload and a client old enough not to cope.

## A nullable column took every cached package to 500 — 2026-09-12

Carrying the upstream's spelling on the cache row needed a column. It was added the way EF scaffolds one:

```
migrationBuilder.AddColumn<string>(name: "Id", ..., nullable: true);
```

The property is `public string Id { get; set; } = ""`. Every row written before the column existed reads
back NULL, EF assigns it into that non-nullable string regardless, and the first `.Length` on it threw.
42 of 55 live rows were in that state, and **ten out of ten sampled packages answered 500** on their
registration index. `Find-Module` reports that as "the package does not exist", which is how it was first
described.

It looked like it was healing, and it was not. Each background refresh rewrote one row, so failures
rotated between packages rather than clearing: `powershellget` recovered while `dbatools.library` broke,
minutes apart. Sampling one package at a time would have suggested a flake.

### What it took to put right

- Coalesce where the database is read, so no caller can be handed a null id.
- `IsNullOrEmpty` instead of `.Length` at the two sites that took it.
- Declare the column required with an empty default - and **fill the existing nulls first**. EF scaffolded
  a bare `AlterColumn` to NOT NULL; on SQLite that is a table rebuild which copies existing data, so a
  NULL fails the migration. This application migrates on startup, so that would have turned a broken page
  into a container that never comes up. Both migrations now run
  `UPDATE CachedUpstreamIndexes SET Id = '' WHERE Id IS NULL` before the alter.

Verified after deploying: the same ten packages all answer 200, no row holds NULL, and the log is clean.

### The test that could not have caught it

The first version wrote `Id = null` into the row. Once the schema was fixed that raises
`NOT NULL constraint failed`, so the test proved only that SQLite enforces its own constraint - it could
not express the defect it was written for. The migration turns those rows into empty strings, so *empty*
is the state real rows reach, and that is what the test pins now.

The deeper miss is the same one as the paged-registration defect earlier today: every test writes its rows
through today's code, so no test ever produces a row that an older build left behind. Nothing in the suite
can see a migration-shaped defect unless it is written to.

## Un-caching a package, and two alarms I raised wrongly — 2026-09-12

The tester's "no way to clean up cached versions" is now an action. A cached copy wins the merge by
design, so one held version keeps being answered - and keeps being latest - however the gallery moves on.
PowerShellGet 2.2.5.1 is what that looks like from the outside.

`PackageIngestionService.UncacheAsync` removes every `Cached` version of one id and its files, reusing
`PurgeAsync` per version rather than growing a second cleanup path - the symbol-file removal lives there
and is easy to forget. Versions **pushed** to the feed are untouched, the same rule withdrawal
reconciliation already follows: what somebody published here is nobody else's to remove. The button sits
on the package page, says how many it will remove, and is one click rather than a typed confirmation
because nothing is lost - the next download fetches the versions back.

The test drives the service, not the button: the fixture with a browser harness has no upstream, and the
one with an upstream has no harness. What it pins is which rows and files go and which survive, and that
the package still resolves from upstream afterwards. The endpoint itself is a thin wrapper on the
`versions/delete` template and is *not* covered by a test.

### "The listed flag is flapping" — it was not

The tester reported PowerShellGet was no longer being cached. Reading a filtered log tail I saw
listed → unlisted → listed on 2.2.5.1 and called it a flap. Counted over six hours it is **2 withdrawn and
2 offered-again, both for that one version**: the reconcile correctly mirroring a version the gallery
hides, with one transient re-list across a restart. Live state is 49 cached listed, 2 cached unlisted, and
both unlisted ones are genuinely hidden upstream.

### "v2 is advertising Listed: false" — also not

Same mistake, ten minutes later: a `tail` of the Atom showed a run of `Listed: false` and I called it a
defect in what we advertise. Across the whole document it is 27 true against 13 false, and the two that
decide it are right - 2.2.5 reports `Listed: true, IsLatestVersion: true`, 2.2.5.1 reports false. The
false rows are prereleases the gallery hides. Twice in one session a truncated tail produced a defect that
was not there; the discriminator was always the specific version, never the tail.

### What was actually wrong: nothing here

Caching works, proven on the deployed build against a package nothing had ever fetched: one v2-shaped
download of `Carbon 2.11.1` (781,650 bytes, 2.0s) wrote `Carbon 2.11.1 | Cached` and took the table from
55 rows to 56. The same held for PowerShellGet 2.2.5 - requesting it created the row that was missing.

So the install that "did not cache" never reached this server. It took 386 ms where the cold find beside
it took 59.7 s, and PSResourceGet keeps its own package cache: a module it has already downloaded installs
without asking us again. Unconfirmed until the tester checks - the proxy has no access log, so there is no
record on our side either way.

## Nothing was logged, so nothing could be answered — 2026-09-12

Asked whether a colleague's tests are visible on our side. They are not: this server had produced **eight
log lines since start**, none of them a request. `Microsoft.AspNetCore` is pinned to `Warning` in
appsettings, which suppresses the request-logging category, and nothing wired up HTTP logging. That is why
"is it reaching us at all" had to be argued from download counters and how long an install took.

### What the server being replaced actually does

Checked rather than assumed, and my assumption was wrong. I first cited its 40,451 log lines as evidence it
logs all traffic; reading them, every one is scheduler chatter - Execution Dispatch, Failover Detection,
Drop Path Monitor, at 1,331 lines an hour. **Not one is a package request.** It keeps two separate things,
both opt-in and off by default: a W3C HTTP request log on disk (`ENABLE_REQUEST_LOGGING=true` in its
container; 5 MB x 60 files) and a per-feed "record individual downloads" checkbox writing to a table of
feed, package, version, user, IP and agent - which its own documentation warns reaches gigabytes with no
built-in pruning.

Neither records whether a request was served from cache or fetched from an upstream. Their own guidance is
to infer it from `time-taken`. That is the one field that would have answered today's question outright.

### What was added

`FiGet:Logging:Requests`, default off, matching their opt-in posture. One structured line per request:
method, path, query, status, duration, caller address, forwarded address, who, and user agent. Health
probes and framework assets are skipped or they would be most of the log.

Both addresses are recorded deliberately. Forwarded headers are honoured only for proxies the runtime
trusts and the default trusts loopback alone, so behind a reverse proxy on another address the remote
address is the proxy, not the caller. Logging the raw header beside it means the line is never quietly
wrong about who asked.

"Who" comes from `FeedAccess`, not from the middleware: that is the single place every protocol request
resolves a feed and a token, so attribution exists once and names the token rather than an address.

Still open, and deliberately not built into this change: persisting per-download records with a
cache-versus-upstream flag behind an admin page. That is a schema change and a retention question - the
warning above is what happens when retention is an afterthought.

### The audit log is a separate thing, and is now designed

Asked whether the new request log covers auditing. It does not, and should not: it answers whether a client
reached this server, while an audit answers who changed something. Decided 2026-09-12 and written up in
docs/backlog.md - admin changes, package lifecycle and authentication events, in a database table behind an
admin page, with configurable automatic pruning. To be built after the current testing settles.

Per-download records carrying cache-versus-upstream origin were considered and deliberately left out of
that entry: heaviest by volume, and they belong with usage statistics rather than with an audit trail.

## Two versions counted, one shown - and a page size worth choosing - 2026-09-12

Reported from the package page: the admin panel offered to un-cache 2 versions of PowerShellGet while the
list above it showed 1.

Both numbers were right. `CachedCount` counts every cached version; the table filters to listed ones,
because this page hides what the gallery hides. PowerShellGet has two cached copies, 2.2.5 and 2.2.5.1, and
the gallery unlists the second - so it sat inside the "52 unlisted versions hidden" line while the button
correctly promised to remove both. The panel now says so: "1 of them is not in the list above, because the
gallery unlists that version". The count itself stays honest about what the action does.

Not covered by a test, and worth stating plainly: the panel is admin-only and needs a version that is both
cached and unlisted, which means an admin session on a proxy feed - the browser harness and the upstream
stub live in different fixtures. The same split already left the un-cache endpoint untested. What does
protect it is that both numbers now read the same `Listed` flag, so they cannot drift apart silently.

### Rows per page

Also asked for: more than 50 rows at a time. The list now offers 50, 100, 250 and 500, carried in a `per`
query parameter and kept across paging links. Capped deliberately - rows are rendered server-side, and
PnP.PowerShell has 2098 versions - and a size that is not offered falls back to 50, so a hand-typed
`per=100000` cannot render the lot. Two integration tests pin the default and the fallback.

Verified live on dbatools, which has 990 listed versions: the chooser renders, `per=250` gives "1 to 250 of
990 versions", and `per=9999` gives "1 to 50 of 990".

### The cold-start window, measured at last

Chasing the above turned up something better evidenced. After a restart the description cache is empty, so
every version looks listed until the first described refresh lands. Watched deliberately across a deploy:
at 20:48:20 PnP.PowerShell read "1 to 50 of 2098 versions" with no hidden line; at 20:48:47 the same page
read "1 to 36 of 36 versions, 2062 unlisted hidden". Twenty-seven seconds.

I had called this twice before and got it wrong both times - first "flapping", then "minutes for a large
package". It is one cycle per restart, it corrects itself, and it is under thirty seconds. Recorded in
docs/backlog.md with the fix worth making: re-listing should require positive evidence, so a cold cache
means "no news" rather than "everything is fine".

### Used in production before the write-up was finished

Fifteen minutes after the deploy, the request log answered a question about itself. Rows were disappearing
from the database between two of my own queries, and rather than guess, the new log said what happened:

    "Method":"POST","Path":"/admin/feeds/gallery/packages/uncache"
    caller=203.0.113.10 who=user:tester
    "Un-cached 1 version(s) of PowerShellGet from feed gallery; it follows its upstreams again."

The tester used the un-cache button. It worked, it wrote its line, and the row count moved. That is also the
live proof of attribution that was missing earlier: the token path stayed unverified because tokens are
stored hashed and none should be minted to test with, but the signed-in path shows plainly as
`who=user:tester`.

His session: 80 requests, 79 answered 200 and one 302 - the un-cache post redirecting back. No errors.

PowerShellGet is cached again already, both versions, files and all. That is the intended behaviour and
the panel says so: un-caching is not a delete, and the next download fetches the versions back.

One honest loose end. The panel offered to remove 2 versions and the action removed 1. The count is a
snapshot taken when the page renders, and the action removes whatever is cached when it runs, so the two
can differ if the cache changes in between - which it does constantly on a feed somebody is testing
against. The exact interleaving cannot be reconstructed from the log and is not worth inventing. The
action is right either way: it removes the cached versions that exist at the moment it runs.

## The flapping is fixed at the cause, and there is an audit log - 2026-09-12

### Re-listing now needs to be told, not just not-told

`ReconcileWithdrawnAsync` decided between two answers: still offered, or withdrawn. A cold description
cache - every restart - produced neither, and the code read that silence as "still offered", so a cached
copy the gallery hides was listed again on every start. While it was listed it could win "latest", which is
the single thing that method exists to prevent.

It now distinguishes three answers. Absent from the upstream's version list still withdraws, on presence
alone. Listing again requires the upstream to actually describe the version as listed. Null - nothing
described - means no news, and nothing is changed on that basis in either direction.

Proven by falsification, because a test that passes before and after proves nothing. The stub upstream
gained a `Describes` switch that models exactly what a restart produces: the version list read back from
the database, the descriptions gone. Against the old logic the new test fails - the hidden version comes
back listed and reclaims latest. Against the fix it passes.

### Deleting the cached copy instead: considered, argued against

The suggestion was to remove a cached copy outright when the upstream unlists it, which would also stop the
flapping. Three reasons not to:

- It would not finish the job. The other half of the cold-start effect is upstream versions, whose listed
  flag comes from the same descriptions - PnP.PowerShell shows 2098 "listed" versions for the first seconds
  after a restart, and no cached row is involved in that.
- It trades a flag flip for real churn. Unlisted still means downloadable by exact version, so a deployment
  pinned to that version fetches it, this server caches it, the next reconciliation deletes it, and round
  again.
- It throws away the copy the cache exists for. Unlisted is often the step before removal. If the gallery
  drops the version later and this server deleted its copy, every machine pinned to it breaks with no
  internet path to recover - and serving machines that have no internet access is the whole point.

If it is wanted, the right shape is a per-feed policy beside `DeletionBehavior` - unlist or drop on
withdrawal - not a change of default.

### The audit log, console half

Who changed what: feeds created, edited, deleted; upstreams added and removed; tokens issued and revoked;
the theme changed; packages pushed, deleted, relisted, pulled and un-cached; sign-ins, including refused
ones. Each line carries the actor and the caller address, sharing one `RequestActor` implementation with
the request log so an audit line can never name a different person than the request line beside it.

No bespoke on/off key. The category is `FiGet.Audit` and the standard log-level configuration governs it,
so it is on wherever Information is on and `Logging:LogLevel:FiGet.Audit=None` silences it. On by default
is deliberate: an audit trail that must be switched on in advance is not there on the day somebody asks
what happened, and this is a few lines a day rather than a few per request.

The un-cache line carries how many versions actually went, which is the number that was missing when the
panel offered two and the action removed one.

Console only, as agreed. A container log rotates and is lost; the database table and admin page are still
described in docs/backlog.md.

### Unrelated flake worth knowing about

`Readers_of_the_same_stale_catalogue_cause_one_refresh` failed once during this work and then passed alone,
as a class, and in a full run. It counts background refreshes and allows at most two; under a loaded
machine a third can land inside its window. Pre-existing, not introduced here, and it will eventually do
this in CI.

### Both verified on the live instance

Deployed and checked rather than assumed.

**The re-listing fix holds.** This is the first restart since it landed, and the two cached copies the
gallery hides - `PnP.PowerShell 1.9.61-nightly` and `PowerShellGet 2.2.5.1` - are both still `listed=0`,
with **zero** re-list and zero withdrawal events since the container came up. Every previous start produced
exactly one re-list followed by one correction, so the absence is the result.

**The audit log writes real lines.** The count was zero on the previous image, so anything now is from this
build. A refused sign-in produced:

    signin.refused unknown token by anonymous from 203.0.113.11

under category `FiGet.Audit`, with `Action`, `Subject`, `Actor` and `Caller` each rendering as their own
field. That is the case worth having: a client presenting a key that no longer works was previously
invisible.

The first attempt to produce that line was wrong and is worth recording. Posting the login form directly
returned **400** - the form is behind antiforgery, so the request was rejected by middleware before it ever
reached the handler, and no audit line could fire. Read quickly, "400 and no audit line" looks like a
broken audit log. The honest read was that the probe never ran the code. Repeating it through the real form
- fetch the page, carry the cookie, send the antiforgery token and `_handler` - answered 200 and wrote the
line.

Also verified after the deploy: no errors at start, health probes still absent from the request log, the
un-cache route still behind sign-in, package pages and both protocols answering, the page-size chooser
working, and 58 rows intact. The request log is meanwhile showing the colleague's client by name -
`PSResourceGet/1.1.0.1 PowerShell/5.1.26100.9168` - which is what it was built for.

### Half the request log was a stylesheet

Reported while watching it: `/themes/cobalt.css` on every page view. Counted on the live instance, 31 of 65
request lines were browser assets and 15 of those were that one stylesheet — so roughly half the log was a
browser re-fetching the same two files, burying the lines that say what a package client did.

The filter skipped health probes and the framework's paths but nothing else. It now also skips stylesheets,
scripts, icons and fonts, matched by extension rather than by folder because the static assets are served
from the web root with a content hash in the name (`/app.9eycm9ixdl.css`), so there is no prefix to match.

One rule matters more than the filter: **anything under `/nuget` is never skipped**, whatever it is named.
A package may legitimately be called `something.css`, and an extension test that could swallow a package
download would hide exactly what this log exists for. That is the half the test pins — it asks for a
package id ending in `.css` and requires the line to be there, alongside asserting the real stylesheet is
absent.

What survives is the useful set: package pages, the v3 index, registration documents, sign-ins, and every
protocol call.

## A first install brought no dependencies with it - 2026-09-12

Reported from testing: `Install-Module Microsoft.Entra` installed the module and none of its nine
sub-modules. The second attempt installed everything. The same had happened over v3 earlier.

The request log answered it, which is the first time it has earned its place. Two attempts, minutes apart:

    21:23:05  FindPackagesById id='Microsoft.Entra'        200   2ms
    21:23:08  package/Microsoft.Entra/1.3.0                200 1370ms
    -- nothing else. Not one dependency was asked about. --

    21:23:48  FindPackagesById id='Microsoft.Entra'        200   2ms
    21:23:53  FindPackagesById id='...CertificateBased...' 200   2ms   (and the other eight)
    21:23:55  package/...CertificateBasedAuthentication/1.3.0  200 927ms   (and the other eight)

The client did not fail to install the dependencies. It never learned they existed.

### Why

`UpstreamMetadata` carried description, summary, title, authors, tags, urls, published, downloads and
listed - everything except dependencies. `Describe()` copied all of it onto the placeholder row for an
uncached version, so that row declared no dependencies, and both protocols read dependencies from that one
collection: the v2 Atom writer through `AtomWriter.Dependencies`, and v3 through `BuildLeafItem`, which
groups `v.Dependencies` into `dependencyGroups`. A client reading either concluded the module had none.

On the second attempt the first had already cached the package, its dependencies came from the nuspec by
way of the indexer, and everything resolved.

Proven on a package nobody here has ever cached. For `ExchangeOnlineManagement` this server answered
`<d:Dependencies />` and `"dependencyGroups":[]`, where the gallery answers
`PackageManagement:[1.0.0.1, ):|PowerShellGet:[1.0.0.1, ):`.

For a proxy feed in front of a gallery, an uncached version is *the first install of anything*. This was
the normal case, not an edge one.

### The fix

`UpstreamMetadata` gained a dependency list, `ToMetadata` maps `IPackageSearchMetadata.DependencySets`, and
`Describe()` writes them onto the row. No extra network traffic: those sets already arrive in the call
being made for the description.

The mapping copies `PackageIndexer` exactly - empty framework name for "any", empty range for "any
version", one running ordinal across groups, and a single row with no id for a group that declares none.
That matters more than it looks: if upstream-derived dependencies differed in shape from indexed ones, a
package would report different dependencies before and after being cached, which is a worse defect than
the one being fixed.

Two tests, one per protocol, both on a version nothing has cached.

### Noted while measuring

`UpstreamMetadataCache` is an unbounded dictionary - `Get` evicts only the key it is asked for, and nothing
sweeps it. Dependencies add to what it holds: Microsoft.Graph averages about 2 KB of dependency text per
version, so roughly 210 KB across a hundred versions. Small next to the tags already held, but it is
growth on something already unbounded, and the 101 MB incident came from this same cache.

### Verified live, and a sixth tail that lied

Deployed and checked against packages this server has never cached, which is the only honest test: a
populated answer can then only have come from the upstream.

    Az.Storage           ->  Az.Accounts:[1.8.0, ):
    ExchangeOnlineManagement 3.10.1 ->  PackageManagement:[1.0.0.1, ):|PowerShellGet:[1.0.0.1, ):
    Microsoft.Entra 1.3.0    ->  all nine sub-modules, pinned [1.3.0, 1.3.0]

Byte-identical to what the gallery answers for the same versions, on both protocols - v2 through
`d:Dependencies`, v3 through `dependencyGroups` in the registration.

That also settles the open risk: the gallery is a **v2** upstream, and the tests only ever proved the
mapping against a stub. NuGet's v2 metadata path does populate `DependencySets`, so the fix works against
the real thing and not just against the fixture.

`MicrosoftTeams` answers with no dependencies here - and the gallery answers the same. Correct, not a gap.

**The sixth tail.** `ExchangeOnlineManagement` looked broken on v2 long after its refresh had landed: the
last entries in the document carried no dependencies while v3 showed them. The document is ascending, our
default page is 40 entries, and the tail of the first page is version 1.0.1 and 2.0.1 - releases that
genuinely declare nothing. The 3.x releases that do declare dependencies were simply not on the page I
sampled. Checking the version by name rather than the end of the list showed it had been right all along.

Six times today a truncated view has produced a defect that was not there: `grep -c` counting lines, a
`grep -B2` walking into a neighbouring entry, a log tail read as a flap, a line count read as traffic, and
twice a version list read from its end. The rule that has worked every single time is to check the
specific value - this version, this field - and never the tail.

## The facts that matter now survive a restart - 2026-09-13

Three defects had one cause: the version list an upstream reported lived in the database, but everything it
said *about* those versions lived only in memory. After every restart the descriptions were gone, so until
the first refresh landed a hidden version looked listed, a package with two thousand versions offered all
of them instead of the thirty-six the gallery advertises, and - the one a colleague actually hit - an
uncached version declared no dependencies, so a first install brought none of them with it.

Two facts are now written beside the version list: which versions the upstream does not advertise, and what
each version depends on. Nothing else. Descriptions, summaries and tags stay in memory, because those are
the hundred megabytes that caused the out-of-memory incident recorded above - against tens of kilobytes for
these. The encoding is one line per version: the version, a tab, then `id:range:framework` joined by pipes,
which is the triple the v2 protocol already puts on the wire. A range carries spaces and commas but never a
tab, colon or pipe, so it round-trips without escaping.

One rule guards the write: **descriptions empty leaves both columns alone**. An upstream that answered
without describing anything has said nothing new, not that the package suddenly depends on nothing. Without
that, one undescribed refresh would erase exactly what this exists to keep.

The migration adds both columns `nullable: false` with `defaultValue: ""` in a single `AddColumn`, on both
providers. Deliberately not the scaffolded add-nullable-then-alter: that is what broke startup this
morning, and `MigrateOnStartup` means a bad migration turns a working container into one that never comes
up.

### The memory this was supposed to save

Persisting the facts does not shrink memory on its own, and the ask came from operators who watch pod
limits. So the cache is bounded: `FiGet:Connector:MaxDescribedPackages`, 500 ids per replica, dropping the
oldest-written beyond that. It was an unbounded dictionary that evicted only a key somebody happened to
read while stale - nothing swept it, and every replica held its own copy.

Bounding is safe now in a way it was not before. What a listing needs to be correct is in the database;
what the cache holds is the text beside it. An evicted entry costs a listing its description until the next
refresh, never its meaning. That ordering matters: the bound would have been a bug before the persistence.

Oldest-written rather than least-recently-used, because `Get` does not touch the timestamp and making it do
so would mean a write on every read of a cache whose point is to be cheap.

### Also

`FindPackagesById` defaults to 100 entries instead of 40 when a client does not ask, matching the gallery.
Every client this exists for sends `$top` explicitly, so this is about answering like the thing we replace.

### A trap that caught me three times in one file

Writing C# escape sequences through a shell heredoc: `'\n'` and `'\t'` arrived as a real newline and a real
tab inside character literals, which is "Newline in constant" and "Empty character literal". I fixed it,
reintroduced it in the fix, and reintroduced it again in the constants meant to remove it. What worked was
building the characters with `chr(10)` and `chr(9)` for the *searches*, and using an editing tool that
passes strings through untouched for the *replacements*. The note in memory about this was right; I applied
it to half the problem.

### And I put the flapping straight back in - 2026-09-13

Minutes after deploying the persistence, a number I had been asserting was zero came back as one:

    22:06:42  container started
    22:06:45  PnP.PowerShell 1.9.61-nightly is offered upstream again
    22:07:25  PnP.PowerShell 1.9.61-nightly was withdrawn upstream

Three seconds after a restart, a version the gallery hides was listed again - and for the forty seconds
until its first refresh it was eligible to be "latest". That is precisely the defect fixed this morning,
reintroduced by the change meant to make restarts safe.

The cause is a default value. The migration sets both new columns to empty, so a row written before they
existed is indistinguishable from a row whose upstream hides nothing: both are `""`. The store parsed empty
into an empty *set*, the connector read "not null" as "we were told", and an empty set says nothing is
hidden - so every hidden version was re-listed. All 71 existing rows were in that state, and each would
have done it on every restart until refreshed.

Empty now means "we were not told". Only content counts as having been told, and absence is reported as
null so it flows down the "no news" path that already existed. A package that genuinely hides nothing and
declares nothing reads as no news until a refresh describes it - conservative, and it changes no flag,
which is the only direction that cannot cause this.

Proven by falsification rather than by going green: with the old reading the new test fails, with the fix
it passes.

**What this cost and what it is worth.** Two ideas that are the same value in the database - "none" and
"unknown" - were the same string, and I did not notice until production told me. The persistence itself
was tested, deployed and verified working; this hid underneath it, in the default on a column. Worth
remembering next time a migration adds one: the default is a value, and somebody downstream will read
meaning into it.

**Verified live.** Deployed, restarted, and the two packages whose rows still carry blank facts -
`powershellget`, holding a hidden 2.2.5.1, and `dbatools` - were viewed straight after start: **zero**
re-list events, zero withdrawals, both hidden copies still unlisted, no errors. The previous build produced
a re-list three seconds after the same restart, with 65 of 71 rows in a state that would repeat it.

## Tables stretched down the page on a phone - 2026-09-13

Reported from a phone: the feeds page showed its header, then an empty panel filling the screen, then one
feed pinned near the bottom and the others nowhere. Same signed in and out.

Reproduced at 390 px with a headless browser before changing anything. Every row was roughly a thousand
pixels tall. "gallery" had broken into "galle / ry" and sat in the vertical middle of its own row; the
second feed was far below, off-screen. So nothing was missing and nothing was pinned to the bottom - the
screenshot was the top half of one enormous row.

**The cause is one property doing more than it says.** `overflow-wrap: anywhere` lets text wrap, but it
also shrinks the cell's *minimum* width to a single character, and the table's automatic layout takes that
literally. The first column wraps anywhere on purpose - commit `912115f` added it so long module ids stop
pushing wide tables past their container on desktop - and the v3 source URL in the last column does too.
On a phone the four columns that never wrap claimed the whole width, those two collapsed to one character,
and a fifty-character URL one character wide is a very tall row. `vertical-align: middle` put the name in
its centre.

The obvious fix was the wrong one. Swapping to `overflow-wrap: break-word` does not shrink min-content, so
an unbroken id like `Microsoft.Graph.Authentication` would refuse to wrap and the desktop overflow
`912115f` fixed would come back. Instead both columns keep wrapping but gain a floor: `min-width: 8rem` on
a table's first cell, and `14rem` on a URL inside a table. On a phone the table is then wider than the
screen and scrolls sideways inside its wrapper - which already had `overflow-x: auto` - rather than
stretching downwards. The URL floor is scoped to tables, because `.fg-url` is also used in panels on the
feed settings and tokens pages where it has no room to spare.

Verified before deploying, by rendering the live page with only the two new rules added, and again live
afterwards at 390 px on the feeds list, a feed's package list and a package's versions. Desktop at 1280 px
is unchanged apart from a short first column being slightly wider; everything still fits on one line.

**Still there, and not caused by this:** on a phone the feed page's header URL, its search bar and the
"shown of" line run past the right edge, because they are not table cells. Long ids now wrap mid-word
("Microsoft.Entra.A / pplications"). Both recorded in docs/backlog.md.

## An install that fetched the wrong version: not ours, and already reported - 2026-09-13

A colleague installed PowerShellGet over v3 with `Install-PSResource` and got a folder named `2.2.5` whose
manifest said `ModuleVersion = '2.2.5.1'`. The same command over v2 was correct. It took most of a night to
find, and most of the wrong turns were mine; they are recorded because they are the useful part.

### What it is

PSResourceGet resolves the version correctly - which is why its prompt and the folder say 2.2.5 - and then
picks the download URL with a **substring match** on the version string. Its entries are in descending
order, so for 2.2.5 the first URL containing the text `2.2.5` is `.../2.2.5.1/powershellget.2.2.5.1.nupkg`.
`2.2.5` is a text prefix of `2.2.5.1`. That one comparison explains every detail observed.

Reproduced on this workstation with **PSResourceGet 1.2.0 on PowerShell 7.6.5**, newer than the colleague's
1.1.0.1 and on a different operating system, using `Save-PSResource` into a scratch folder:

| requested | folder | manifest inside | the longer sibling |
|---|---|---|---|
| 2.2.4 | 2.2.4 | **2.2.4.1** | 2.2.4.1, **listed** |
| 2.2.3 | 2.2.3 | 2.2.3 | none |
| 2.2.5 | 2.2.5 | **2.2.5.1** | 2.2.5.1, unlisted |

The 2.2.4 row is the one that settled it: **both versions are listed** and it still delivers the wrong
package. The 2.2.3 row is the control that shows the harness was sound. The server log agrees from the
other side - asking for 2.2.4 cached 2.2.4.1 here, because that is what the client actually fetched.

Already open upstream: PowerShell/PSResourceGet **#1657** (same symptom, `2024.5.20.1` folder holding
`2024.5.20.12`), **PR #2019** (unmerged, replaces the substring match with a parsed-version comparison),
and **#2030**, filed 2026-09-10.

### Why the server was not changed

This server is correct on every point that could be checked, and matches nuget.org:

- The registration index carries unlisted versions with `listed: false` and `published: 1900-01-01`, the
  latter mirrored from what the gallery itself reports. nuget.org does the same (30 of 84 leaves for
  newtonsoft.json).
- The flat-container version list includes unlisted versions, as nuget.org's does (84 of 84), and as the
  NuGet documentation requires: that list "contains both listed and unlisted package versions".
- Search already honours `listed` and reports 2.2.5.

A workaround was proposed - hide unlisted versions - and rejected on evidence. It would not have helped: the
fault is prefix matching, not listedness, and 2.2.4 against 2.2.4.1 breaks with both listed. It would also
have broken installing an unlisted version by exact version, which is what unlisting is meant to preserve,
and taken the server off the reference implementation for no protection.

The fleet's pinned stack is unaffected: PowerShellGet 2.2.5 over v2, where exact-version installs of
2.2.5.1 resolve and download correctly. Only PSResourceGet over v3 is hit, and only when the requested
version is a text prefix of a longer sibling.

### The wrong turns, in order

1. **"He was on an old build."** Wrong: the fetch was at 22:38:58 on a container started 22:11:42.
2. **"The unlisted flag is flapping again."** Wrong: 2.2.5.1 stayed unlisted throughout.
3. **"PSResourceGet ignores `listed`."** Plausible, consistent with every observation, and wrong - it is
   what made "hide unlisted versions" look like a fix. It was the exact-version test, where the client
   refused an unlisted version it had happily downloaded as "latest", that showed the flag *was* read.
4. **"The flat-container index is where it chose."** Wrong: the client never fetched it; the choice came
   from the registration index.

The misdirection came from real evidence each time. What finally separated cause from coincidence was a
test built to fail differently under each explanation - two listed versions - rather than another
observation that fitted them all.

### Correction: the phone clipping outside tables was the camera, not the page

The section above says the feed page's header URL, search bar and "shown of" line run past the right edge
on a phone. That was wrong, and the measurement behind it was wrong in a way worth knowing.

Chrome's headless mode now drives a real browser window, and a real window has a minimum width - about 500
px. Asking for `--window-size=390` laid every "phone" page out at roughly 489 px and then cropped the
screenshot to 390, so anything between 390 and 489 looked cut off. It was cropped, not overflowing. The clue
was a script reporting `document.documentElement.clientWidth` as 489 on a page asked for at 390.

Re-measured at a genuine 390 px, by loading each page inside an iframe exactly that wide, with a script
reporting any element whose right edge passes the viewport while its parent's does not:

    home   viewport 390   tallest row 73 px   nothing sticks out
    feed   viewport 375   tallest row 74 px   nothing sticks out   (375: a vertical scrollbar)

So the search bar, header URL and result count fit. And the table fix is now confirmed at a true phone width
- the tallest row is 73 px where it was about a thousand - which needs saying, because its earlier check
went through the same cropped window. Long ids breaking mid-word is real and unaffected: that concerns where
text wraps, not how wide the page is.

## Long package ids break after their dots - 2026-09-13

In a narrow column a package id wrapped at any character, because a table's first column carries
`overflow-wrap: anywhere` so that long ids do not push tables past their container. On a phone that read as
"Microsoft.Entra.A / pplications".

Ids now render through `Display.BreakableId`, which puts a `<wbr>` after each dot. The browser prefers those
natural seams and falls back to breaking anywhere only when a single segment is wider than the column. At a
true 390 px:

    Microsoft.Entra. / Applications                        was  Microsoft.Entra.A / pplications
    Microsoft.Graph. / BackupRestore
    Microsoft.Entra. / CertificateBasedA / uthentication   one segment wider than the column: the fallback

Applied wherever an id is a table's first cell: the anonymous package list, the signed-in grid, the
dependencies table and the unlisted versions page. Not to breadcrumbs or version numbers, which are not
narrow id columns.

**The one thing that had to be right:** the helper returns markup, which bypasses Razor's own encoding, while
an id comes from whoever pushed the package. It encodes first and inserts the breaks afterwards; encoding
never produces a dot, so a break can never land inside an entity. A valid id cannot hold angle brackets, but
a helper that emits markup must be safe without relying on validation somewhere else. So a test feeds it
`a.<script>...</script>.b` and requires that, once the breaks are removed, no angle bracket remains. With the
encoding taken out that test fails; restored, it passes.

The same screenshot - taken through a 390 px iframe, not a cropped window - is also the visual confirmation
of the correction above: the feed page's header URL wraps with its Copy button in view, the search bar and
its filter fit, and the result count wraps onto a second line. Nothing reaches past the edge.

## Find-Module on a package with thousands of versions: measured - 2026-09-13

The backlog entry said to measure before changing anything. Against the live feed, PnP.PowerShell (2098
versions), paging the way PowerShellGet does with `$top=40`:

    first run            53 pages, 44.3 s; first pages ~200 ms, last ~1400 ms
    shuffled page order  skip=0 ~1100 ms, skip=2080 ~1150 ms (descriptions already warm)

The first run looked like cost growing with `$skip`. It was not: that run started just after a restart, and
the descriptions landed part way through, so page position and "is this version described yet" were the same
variable. Asked in random order with everything described, a page costs what its *response size* costs, not
where it sits. That matches the code: `Page()` skips over an already materialised list, which is cheap.

**What a page is made of.** Tags are 92-96% of each response. The heaviest page (skip=2040) is 2541 KB, of
which 2447 KB is the `Tags` element: PnP.PowerShell lists every cmdlet as a `PSCommand_*` and `PSCmdlet_*`
tag, on every version. One `Find-Module PnP.PowerShell` moves about 80 MB.

**Compression does not fix it.** With `Accept-Encoding: gzip` the same page arrives as 331 KB (offline gzip:
313 KB, 88% smaller), but it still takes ~1430 ms against ~1480 ms without. Transfer is not where the time
goes; building and writing tag-heavy entries on the server is.

**Not measured, and not claimed:** where inside the server that time sits (row building, the Atom writer, or
the tag text itself). An attempt to read it from the request log was discarded as unreliable.

**Why nothing was changed.** The cheap-looking fix - send fewer tags - is user-visible. PowerShellGet builds
`Find-Module`'s `Includes` (Function, Cmdlet, Command) from exactly those `PSCommand_*` / `PSFunction_*`
tags and needs the `PSEdition_*` ones, so trimming them silently changes what people see. That is a decision,
not an overnight change. The options are in the backlog entry.

## Admin buttons accepted posts without their antiforgery token - 2026-09-13

Found while testing the asset upload page. Signed in as admin, a form post to `/admin/assets/files/folders`
with no antiforgery token was answered 302 and the folder was created. The same was true of every admin
button: pull, add and remove upstream, relist, delete and un-cache.

**Why.** The pages carry `<AntiforgeryToken />` and the app calls `UseAntiforgery()`, which reads as
protected. It is not: the middleware only records whether the token was valid, and minimal APIs act on
that verdict only while binding a form to a handler parameter. These handlers read their forms with
`ReadFormAsync`, so the verdict was never consulted.

**Why it mattered.** The sign-in cookie is `SameSite=Lax`, which stops a post from another *site* - but a
sibling subdomain is the same site, so any page on another host under the same domain could press these
buttons for a signed-in admin.

**Fix.** One endpoint filter on the `/admin` group validates the token on every non-GET request, from the
`RequestVerificationToken` header or the form. `AdminUiTests.An_admin_button_post_without_its_page_token_changes_nothing`
posts to "add upstream" without the token (400, nothing added) and then with the page's token (302, added).
With the filter's check disabled the test fails with `Expected 400, got 302 Found`; restored, it passes.

## Asset directories - 2026-09-13

Phase 4, core only by decision: download, upload, delete, folders, listing, metadata, and a browse page with
a drop zone. Multipart upload, archive import and export, and remote-URL fetching are in the backlog.

**Contract.** Recorded from the reference server where anonymous reads could reach (empty listing `[]`,
missing file `The specified asset was not found.`, missing metadata `Asset not found.`); everything else from
the reference client library's model files and the published API documentation. The build plan's own sketch
of §4.4 turned out wrong in four places, corrected there. `docs/protocol-assets.md` has the table as built and
what each rule rests on. The write side was not recorded, because writing to the reference instance was not
possible in this session; that is a backlog item.

**Shape.** An asset directory is a feed of kind `Assets`, as on the reference server, so tokens, anonymous
read, deletion and the audit log apply without a second implementation. Rows in a new `AssetItems` table
(migration `AssetDirectories`, both providers); bytes under random ids in `files/assets/`. Each surface sees
only its own kind: `/nuget/{name}` is 404 for a directory and `/endpoints/{name}` is 404 for a package feed.

**Tests.** `AssetDirectoryTests`, 20 tests on SQLite and the same 20 on SQL Server (LocalDB): byte-for-byte
download with the SHA-256 ETag, PUT/POST/PATCH semantics, a Range request answered 206, HEAD, the recorded 404
bodies, listings (including recursive, and a `%` in a folder name taken literally by the SQL prefix match),
case-insensitive paths, scopes (anonymous and read-only tokens refused writes even on an anonymous directory),
idempotent delete removing the stored bytes, full folders deleted only with `recursive`, a file in the way,
metadata with a `ttl` cache header, a 1 MB limit refusing a 1 MB + 1 byte upload and leaving nothing behind,
the browse page for a reader, and the page upload with and without its token. `AssetPathTests`, 13 unit tests.
Suites: unit 84/0; integration 165/0 with SQL Server, 29 + 20 skipped without it.

**Real clients**, against a local instance with a directory named `installers`, a 3 MB random file uploaded to
`tools/runtime/installer.exe`:

    curl 8.16.0 --data-binary PUT               201; again 409, file unchanged
    curl download, anonymous                    SHA-256 equal to the upload
    Windows PowerShell 5.1.26100 Invoke-WebRequest -UseBasicParsing     SHA-256 equal
    Windows PowerShell 5.1 System.Net.WebClient.DownloadFile            SHA-256 equal
    PowerShell 7.6.5 Invoke-WebRequest -Resume, from a 1 MB partial     206, SHA-256 equal

The request log recorded all of them, `.exe` included: the log's browser-furniture filter goes by extension,
and `/endpoints` now joins `/nuget` as never noise.

**UI.** The browse page, signed in, checked in headless Chrome at desktop width and in a 390 px frame: breadcrumbs,
download URL with copy, the drop zone, a "new folder" disclosure, and a table of folders and files with copy
and delete; on the phone the table scrolls inside its wrapper like every other table. The page capture had its
scripts removed, so the drop zone's own drag, progress and confirm-before-replace were not exercised in a
browser; the request it sends is the one the page-upload test sends.

## Pages failed under concurrent load on SQL Server - 2026-09-13

Found while repeating the full suite on SQL Server: the asset browse page tests failed now and then with 500,
never on SQLite. The server log named it: "A second operation was started on this context instance before a
previous operation completed", thrown in `FeedPackages.OnInitializedAsync`.

**Why.** Server-side rendering does not wait for a component's async initialisation before it initialises
the components inside it. `App.razor` reads the chosen theme from the database, and the page inside reads its
feed, both through the request's one database context - so the two queries overlapped. SQLite runs queries
synchronously and never let them; SQL Server did. Present since the theme became an admin setting, and not
specific to asset directories: every statically rendered page that reads the database was exposed, which on
SQL Server - the production database - is all of them.

**Fix.** The theme is read through a scope of its own. `PageRenderTests` loads the feed page and the home page
60 times at once, on both providers. Before the fix, on SQL Server, 45, 51 and 45 of 60 loads failed in three
runs; SQLite passed. After it, 0 of 60 three times, and the full suite passed ten runs in a row on SQL Server.
