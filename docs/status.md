# Status

What is built, how it was demonstrated, and what is known to be missing. Newest first. A check is only
listed as passed when it was run against the code in the commit it names.

---

## Phase 0: record the contract — started 2026-09-11

**State: in progress.** First recording done; fixtures not yet scrubbed or committed.

- **Reference server**: its free edition in one container (embedded PostgreSQL) on a home server, with a
  curated PowerShell feed, a PowerShell feed with a PowerShell Gallery connector, and an asset directory.
  Using a private instance instead of the server being replaced means the recordings contain no
  organisation data.
- **Recorder**: `tools/FiGet.Recorder`, a reverse proxy that writes every exchange (credentials redacted,
  binaries reduced to SHA-256 and length) and passes the Host header through, so absolute URLs in responses
  keep pointing at it.
- **First recording**: Windows PowerShell 5.1 + PowerShellGet 2.2.5 + PackageManagement 1.4.8.1 through
  `tests/FiGet.Compat/Record-PowerShellGetV2.ps1`, 85 exchanges, 26 scenarios passing plus 2 intended
  failures (module not found, duplicate publish). The resulting v2 surface and its consequences for phase 2
  are in `docs/protocol-v2.md`.
- **Also recorded**: PackageManagement's NuGet provider 3.0.0.1 against FiGet's own v3 surface, which found
  the catalog entry bug below.

Getting the recording script to run exposed four client-side facts, now in the build plan's traps list:
PowerShellGet's provider is discovered through `PSModulePath` (importing by path breaks every 2.x cmdlet),
PackageManagement 1.4.8.1 uses its bundled NuGet provider 3.0.0.1, PowerShellGet 2.2.5 publishes with the
dotnet CLI when present and otherwise needs NuGet.exe 4.1 or later, and NuGet 7 clients refuse plain-HTTP
pushes. Windows PowerShell 5.1's manifest template also cannot set a prerelease label by text edit.

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
- **The container image** was not built locally (no Docker on the development machine); CI builds it and
  starts it as UID 1000123456 in group 0 on every push.
- **Compatibility scripts** run manually; they join CI on a Windows runner once the repository is
  public (plan §7.2).

### Next

Phase 0 (recording a reference server's traffic) needs an environment with such a server and has not started. Phase 2 (v2 OData)
can begin from the published v2 knowledge plus the probe found above, but its fixtures must come from
phase 0 before it is called done.
