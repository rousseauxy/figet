# NuGet v2 in FiGet — recorded client behaviour

The v2 contract is what the clients send, not a specification. This file lists what was recorded in phase 0
and the conclusions that follow for phase 2. Raw recordings stay outside git; scrubbed fixtures derived from
them go under `tests/fixtures/`.

## Recording setup (2026-09-11)

| Item | Value |
|---|---|
| Reference server | Free edition, single container, embedded PostgreSQL |
| Feeds | `curated` (PowerShell feed, no connector), `modules` (PowerShell feed, connector to `https://www.powershellgallery.com/api/v2`, connector metadata cache off) |
| Client | Windows PowerShell 5.1.26100, PowerShellGet 2.2.5, PackageManagement 1.4.8.1 with its bundled NuGet provider 3.0.0.1; NuGet.exe 6.11.1 for publishing, no dotnet on PATH |
| Recorder | `tools/FiGet.Recorder`, Host header passed through so every absolute URL points back at the recorder |
| Script | `tests/FiGet.Compat/Record-PowerShellGetV2.ps1` |
| Packages | synthetic `FiGetRecordingTest` (1.0.0, 1.1.0, 2.0.0) and `Microsoft.PowerShell.SecretManagement` from the gallery |
| Exchanges | 85 |

## The whole v2 surface PowerShellGet 2.2.5 used

Every request of the run, reduced to its shapes. Nothing else was requested.

| Request | Used by |
|---|---|
| `GET /nuget/{feed}/` | registration (5 to 7 times), before every download |
| `GET /nuget/{feed}/api/v2/` | registration of a feed, once; the reference server answers **404** and registration still succeeds |
| `GET /nuget/{feed}/FindPackagesById()?id='{id}'&$skip=0&$top=40` | Find-Module by name, with `-RequiredVersion`, `-AllVersions`, `-AllowPrerelease`; Save-Module; Install-Module; Update-Module; Find-Package and Install-Package with `-ProviderName NuGet`; and before every Publish-Module |
| `GET /nuget/{feed}/FindPackagesById()?id='{pattern*}'&$skip=0&$top=40` | Find-Module with a wildcard name, including `*`, before falling back to Search |
| `GET /nuget/{feed}/Search()?$filter=IsLatestVersion&searchTerm='{term}'&targetFramework=''&includePrerelease=false&$skip=0&$top=40` | Find-Module with a wildcard (`searchTerm` is the name without `*`, empty for `*`) |
| `GET /nuget/{feed}/Search()?$filter=IsLatestVersion&searchTerm=' tag:{tag}'&targetFramework=''&includePrerelease=false&$skip=0&$top=40` | Find-Module `-Tag` (note the leading space) |
| `GET /nuget/{feed}/Search()?$filter=IsLatestVersion&searchTerm=' tag:PSCommand_{name}'&…` | Find-Command |
| `PUT /nuget/{feed}/` | Publish-Module (NuGet.exe pushes to the source URL itself), 201 on success |
| `GET /nuget/{feed}/package/{id}/{version}` | every download; `{version}` is the normalised version |

Not requested at all: `$metadata`, `Packages()`, `Packages(Id=,Version=)`, `GetUpdates()`, `/$count`,
`$orderby`, `$inlinecount`, any `$filter` other than `IsLatestVersion`.

## Conclusions for phase 2

1. **The mandatory v2 surface for the Windows PowerShell 5.1 fleet is five routes**: service document,
   `FindPackagesById()`, `Search()`, `PUT` on the root, and `package/{id}/{version}`. The `$filter` parser
   must support `IsLatestVersion`; everything else in the grammar of build plan §4.3 is for other clients
   (nuget.exe, PSResourceGet in v2 mode) and must be proven by their own recordings before it is built.
2. **Paging is `$skip`/`$top` with a page size of 40.** A package with more than 40 versions (Az modules,
   Microsoft.Graph) makes the client follow up; the response must carry the `next` link or the client
   must be recorded paging with `$skip` before this is settled.
3. **`id='pattern*'` reaches `FindPackagesById()`.** The reference server answers it (with no entries here) and the client
   falls back to `Search()`. FiGet must not return 400 for it.
4. **`api/v2/` under a feed root may 404.** The client tolerates it.
5. **Publish-Module checks the feed first**: it calls `FindPackagesById()` and refuses client-side when the
   version already exists or is not higher than the current version, so a duplicate never reaches `PUT`.
6. **Latest flags.** In every recorded answer exactly one entry had `IsLatestVersion` and one had
   `IsAbsoluteLatestVersion`, including the proxy feed after an older gallery version had been cached (16
   entries, latest 1.1.2). The double-latest failure reported on the server being replaced did **not**
   reproduce on the reference server with the connector metadata cache off. Its conditions (server version,
   connector cache settings) are still to be found.

## Not yet recorded

- A real prerelease: the manifest edit in the first script did not set `Prerelease` on Windows PowerShell
  5.1, so "2.0.0-beta1" was published as stable 2.0.0. Fixed in the script; needs a new run.
- A package with more than 40 versions (paging).
- `Microsoft.Graph` install and update through the proxy feed (connector acceptance test B).
- PSResourceGet against the v2 root (`/api/v2` suffix) and nuget.exe against v2.
- Authenticated feeds (anonymous read was on for both feeds).
