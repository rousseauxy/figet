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
