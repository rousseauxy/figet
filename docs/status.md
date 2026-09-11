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
| Minimal UI: feeds (list, create), packages (search, browse, versions, install snippets), tokens (create, revoke) | `src/FiGet.Web/Components` |
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
- **The UI browses and searches, but neither downloads nor uploads.** Package pages carry install
  snippets for the dotnet CLI and PSResourceGet only, hard-coded rather than per-feed templates,
  and there is no download link and no `Install-Module` snippet for the Windows PowerShell 5.1
  fleet. All of that is phase 4 in the build plan.
- **The container image** was not built locally (no Docker on the development machine); CI builds it and
  starts it as UID 1000123456 in group 0 on every push.
- **Compatibility scripts** run manually; they join CI on a Windows runner once the repository is
  public (plan §7.2).

### Next

Phase 0 (recording a reference server's traffic) needs an environment with such a server and has not started. Phase 2 (v2 OData)
can begin from the published v2 knowledge plus the probe found above, but its fixtures must come from
phase 0 before it is called done.
