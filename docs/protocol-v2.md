# NuGet v2 in FiGet — recorded client behaviour

The v2 contract is what the clients send, not a specification. This file lists what was recorded in phase 0
and the conclusions that follow for phase 2. Raw recordings stay outside git; scrubbed fixtures derived from
them are in `tests/fixtures/` (format described there).

Recordings so far: PowerShellGet 2.2.5 (419 exchanges, including prerelease, a 144-version module and
Microsoft.Graph with its 38 pinned dependencies), nuget.exe 6.11.1, and PSResourceGet 1.2.0 against a v2
feed root.

## Recording setup (2026-09-11)

| Item | Value |
|---|---|
| Reference server | Free edition, single container, embedded PostgreSQL |
| Feeds | `curated` (PowerShell feed, no connector), `modules` (PowerShell feed, connector to `https://www.powershellgallery.com/api/v2`, connector metadata cache off) |
| Client | Windows PowerShell 5.1.26100, PowerShellGet 2.2.5, PackageManagement 1.4.8.1 with its bundled NuGet provider 3.0.0.1; NuGet.exe 6.11.1 for publishing, no dotnet on PATH |
| Recorder | `tools/FiGet.Recorder`, Host header passed through so every absolute URL points back at the recorder |
| Script | `tests/FiGet.Compat/Record-PowerShellGetV2.ps1` |
| Packages | synthetic `FiGetRecordingTest` (1.0.0, 1.1.0, 2.0.0) and `Microsoft.PowerShell.SecretManagement` from the gallery |
| Exchanges | 85 in the first run; 419 in the second (2026-09-12), which added a real prerelease, Pester (144 versions) and Microsoft.Graph 2.30.0 then 2.39.0 |

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
2. **Paging is `$skip`/`$top` with a page size of 40, driven by the client.** The reference server sends
   no `next` link. PowerShellGet requests the first page twice, then keeps asking for further pages
   (`$skip=40`, `80`, `120`, …, several at once and out of order) until pages come back empty. FiGet must
   honour `$skip`/`$top` on `FindPackagesById()` exactly and return an empty feed past the end.
3. **`id='pattern*'` reaches `FindPackagesById()`.** The reference server answers it (with no entries here) and the client
   falls back to `Search()`, with the `*` stripped from `searchTerm` (empty for a bare `*`). FiGet must not
   return 400 for it — and must not implement wildcard semantics there either: answering it as a literal id
   with no entries is what the reference does and what the client is waiting for before it falls back.
   **Only discovery ever sends a wildcard.** Verified 2026-09-12 against PowerShellGet 2.2.5:
   `Install-Module` and `Save-Module` reject a wildcard name *client-side* — "the specified name … should
   not contain any wildcard characters" — before any request is made, while `Find-Module` passes name
   validation and goes to the network. Pointing all three at a repository that does not exist tells them
   apart: the first two fail on the name, `Find-Module` fails on the repository. So a wildcard never
   reaches a download path, and there is nothing to implement for one there.
   What pulls every `Microsoft.Graph.*` module is `Install-Module Microsoft.Graph`, with no wildcard at
   all: the meta-module's pinned dependency closure, which the client resolves itself and fetches id by id
   — 39 downloads in `meta-save-module-old-version.json`. That is dependency resolution, not wildcard
   expansion, and the two are easy to confuse from the outside.
4. **`api/v2/` under a feed root may 404, and should.** The client tolerates the 404 - but when that probe
   *answers*, `Register-PSRepository` stores `{source}/api/v2/` as the repository's `SourceLocation` instead of the
   URL it was given, and sends every later query there. Not visible against the reference server, which answers
   404; found on 2026-09-13 by running the same client against FiGet, which then served the alias's service
   document. Ansible's `win_psrepository` compares `SourceLocation` with the configured URL as a string, so a
   fleet would report "changed" and re-register on every run. FiGet now answers that probe with 404 as well.
5. **Publish-Module checks the feed first**: it calls `FindPackagesById()` and refuses client-side when the
   version already exists or is not higher than the current version, so a duplicate never reaches `PUT`.
6. **Latest flags: see the next section.** With fewer versions than one page (SecretManagement, 16) the
   reference server gets them right. With more, it does not.

## Paging and latest flags: the double-latest failure, reproduced

The failure reported on the server being replaced ("it returns both the cached version and the gallery
version, and Update-Module / Microsoft.Graph breaks") reproduces on the reference server as soon as a package has
more versions than one page. Recorded evidence, proxy feed with a PowerShell Gallery connector:

| Scenario | Page (`$skip`) | Entries | Flagged `IsLatestVersion` |
|---|---|---|---|
| Microsoft.Graph, nothing cached | 0 / 40 / 80 | 40 / 40 / 36 | – / – / **2.39.0** |
| after `Save-Module -RequiredVersion 2.30.0` | 0 / 40 / 80 | 40 / 40 / 36 | **2.30.0** (first entry) / – / **2.39.0** |
| Pester, nothing cached | 0 / 40 / 80 / 120 | 40 / 40 / 40 / 24 | – / – / – / **6.2.0** |
| after `Save-Module -RequiredVersion 4.10.1` | 0 / 40 / 80 / 120 | 40 / 40 / 40 / 24 | **4.10.1** (first entry) / – / – / **6.2.0** |

What the server does, read from these pages:

1. The locally cached version is put **in front of page 0** and flagged latest (for Graph also absolute
   latest), computed over the local packages alone.
2. The connector's versions follow in the connector's own order, which is **not version order** (the last
   Graph page runs 2.2.0 … 2.9.1), with its own latest flag on whichever page it lands.
3. The shift is not compensated in `$skip`: page 0 of Graph used to end at 1.4.2 and now ends at 1.4.0 while
   page 40 still starts at 1.5.0, so **upstream versions silently drop out**.

**Confirmed on a production server** running the same version, 2026-09-11, with two proxy feeds and six gallery
modules of more than 40 versions each. For every module the total number of entries equalled its version count on
the PowerShell Gallery that day, however many versions were cached. Every cached version therefore appears twice
and pushes one upstream version out: Microsoft.Graph with 10 cached versions returned 10 duplicates and only 106 of
its 116 versions. Two entries were flagged latest whenever at least one version was cached, and one when none was.
Locally pushed packages of more than 40 versions had no duplicates and exactly one latest. Their pages were not in
version order either, which the client tolerates because it pages until a page is empty (point 2 above) and never
reads version ranges from page edges.

What the client does with it: `Find-Module Microsoft.Graph` returned **2.30.0** (the cached, older version) as
the latest; `Find-Module Microsoft.Graph.Authentication` returned 2.30.0 as well; `Save-Module
Microsoft.Graph` failed with **"Unable to download, multiple modules matched 'Microsoft.Graph'. Please specify
an exact -Name and -RequiredVersion."** — the reported symptom.

What FiGet does instead (build plan §5, `VersionListBuilder`): one merged list per id over local and upstream
versions, de-duplicated, **sorted by NuGet version before paging**, latest flags computed once over the whole
list, and `$skip`/`$top` applied to that list. The recorded latest flags on proxy feeds are therefore the one
part of these fixtures FiGet must *not* reproduce; the phase 2 fixture comparison exempts them and asserts
exactly one latest per id instead.

## nuget.exe 6.11.1

| Request | Used by |
|---|---|
| `GET /nuget/{feed}/` then `GET /nuget/{feed}/$metadata` | `list` (before searching) |
| `GET /nuget/{feed}/Search()?$filter=IsLatestVersion&$orderby=Id&searchTerm='{term}'&targetFramework=''&includePrerelease=false&$skip=0&$top=30&semVerLevel=2.0.0` | `list {id}` |
| `GET /nuget/{feed}/Search()?$orderby=Id&searchTerm='{term}'&targetFramework=''&includePrerelease=true&$skip=0&$top=30&semVerLevel=2.0.0` | `list -AllVersions -PreRelease` (no `$filter`) |
| `GET /nuget/{feed}/Search()?$filter=IsAbsoluteLatestVersion&searchTerm='{term}'&targetFramework=''&includePrerelease=true&$skip=0&$top=20&semVerLevel=2.0.0` | `search -PreRelease` (no `$metadata` request, `$top=20`) |
| `GET /nuget/{feed}/FindPackagesById()?id='{id}'&semVerLevel=2.0.0` | `install {id}` (latest) |
| `GET /nuget/{feed}/Packages(Id='{id}',Version='{version}')` | `install -Version` |
| `GET /nuget/{feed}/package/{id}/{version}` | every download |
| `PUT /nuget/{feed}/` | `push`; 201, and 403 for a wrong key |
| `DELETE /nuget/{feed}/{id}/{version}` | `delete` (note: not under `api/v2/package`) |

So nuget.exe needs `$metadata`, `Packages(Id,Version)`, `$orderby=Id`, and `semVerLevel` on top of the
PowerShellGet surface, and a delete route directly under the feed root.

## PSResourceGet 1.2.0 in v2 mode (recorded against the PowerShell Gallery)

Registered as `…/api/v2`, PSResourceGet detects `ApiVersion` V2 and speaks a far richer OData dialect than
PowerShellGet: it puts the whole query into `$filter` and adds `$inlinecount=allpages`. Recorded 2026-09-12 with
the recorder rewriting the Host header to the gallery's (downloads and `next` links therefore bypassed it).

| Scenario | Request |
|---|---|
| find by name (latest) | `FindPackagesById()?$filter=Id eq '{id}' and IsLatestVersion eq true&$inlinecount=allpages&id='{id}'` |
| find with `-Prerelease` | `FindPackagesById()?$filter=Id eq '{id}' and IsAbsoluteLatestVersion eq true&$inlinecount=allpages&id='{id}'` |
| find exact version | `FindPackagesById()?$filter=Id eq '{id}' and NormalizedVersion eq '1.0.0'&$inlinecount=allpages&id='{id}'` |
| find version range `[1.0.0, 1.1.0]` | `FindPackagesById()?$filter=NormalizedVersion ge '1.0.0' and NormalizedVersion le '1.1.0' and IsPrerelease eq false and Id eq '{id}'&$inlinecount=allpages&$skip=0&$orderby=NormalizedVersion desc&id='{id}'` |
| find all versions (`-Version *`) | `FindPackagesById()?$filter=Id eq '{id}'&$inlinecount=allpages&$skip=0&$orderby=NormalizedVersion desc&id='{id}'`, then `$skip=100`: pages of 100 |
| find wildcard `Name*` | `Search()?$filter=IsLatestVersion and startswith(Id, '{prefix}')&$inlinecount=allpages&$skip=0&$top=100` |
| find `-Tag` | `Search()?$filter=IsLatestVersion and substringof('PSScript', Tags) eq true and substringof('{tag}', Tags) eq true&$inlinecount=allpages&$skip=0&$top=6000` — **the gallery itself answered 500** |
| find `-CommandName` | `Search()?$filter=IsLatestVersion&searchTerm='tag:PSCommand_{name}'&$inlinecount=allpages&$skip=0&$top=6000` |
| find with `-IncludeDependencies` | one latest-version lookup per id, the dependency resolved with `IsAbsoluteLatestVersion eq true` |
| save | `FindPackagesById()` as above, then `GET package/{id}/{version}` (302 from the gallery to its blob storage) |

Consequences for FiGet's `$filter` parser (build plan §4.3), now proven rather than assumed:

- boolean properties both bare (`IsLatestVersion`) and compared (`IsLatestVersion eq true`);
- `Id eq`, `NormalizedVersion eq|ge|le` with string literals, `IsPrerelease eq false`, joined by `and`;
- `startswith(Id, '…')` and `substringof('…', Tags) eq true`;
- `$filter` and the function parameter `id='…'` present together (both must agree);
- `$orderby=NormalizedVersion desc`, which must mean NuGet version order, not string order;
- `$inlinecount=allpages` (`<m:count>` in the feed) and `$top` values far above any sensible page size (6000),
  which FiGet caps while PSResourceGet keeps paging with `$skip`;
- a 302 redirect for downloads is acceptable to the client.

## PSResourceGet 1.2.0 against a v2 feed root

Registered with a URL ending in the feed name (`/nuget/{feed}/`), PSResourceGet reports the repository's
`ApiVersion` as **Unknown**. It still publishes (`PUT /nuget/{feed}/`), but every find, save and install fails
client-side with "is not a known repository type that is supported", before any request. The reference server
serves no `/api/v2` form of its feeds (404 for every variant), so PSResourceGet can only use it through v3.
FiGet's `/nuget/{feed}/api/v2` alias is what makes v2 usable for PSResourceGet at all.

## Reference server behaviour worth not copying

- **Duplicate pushes are silently accepted** (201) and overwrite the existing version, on PowerShell and NuGet
  feeds alike. FiGet answers 409 unless the feed allows overwrite.

## What FiGet serves (phase 2, 2026-09-12)

Built from the recordings above, in `src/FiGet.Protocol.V2`. Both roots carry the identical surface:
`/nuget/{feed}` for PowerShellGet and nuget.exe, `/nuget/{feed}/api/v2` because PSResourceGet decides the
protocol from the URL suffix and reports `Unknown` without it. The one exception is the service document, which
only the plain root serves: see conclusion 4 above for why `GET /nuget/{feed}/api/v2/` must be a 404.

| Route | Notes |
|---|---|
| `GET /nuget/{feed}` | Service document. A trailing slash is the same endpoint: the router ignores it, so it is mapped once. |
| `GET /nuget/{feed}/$metadata` | Static EDMX for `V2FeedPackage` plus `Search`, `FindPackagesById` and `GetUpdates`. |
| `GET /nuget/{feed}/FindPackagesById()` | `id`, `$filter`, `$orderby`, `$skip`, `$top`, `$inlinecount`, `semVerLevel`. Unknown id: empty feed, 200, which is also the providers' source-validation probe. |
| `GET /nuget/{feed}/Search()` | Adds `searchTerm` (PowerShellGet's ` tag:x` syntax included) and `includePrerelease`. |
| `GET /nuget/{feed}/Packages()` and `Packages(Id='x',Version='y')` | The collection and one entry; an `Id eq` in the filter fetches that package directly. |
| `GET /nuget/{feed}/GetUpdates()` | `packageIds`, `versions`, `includePrerelease`, `includeAllVersions`. No recorded client sends it. |
| `GET …/$count` on the three listings | `text/plain` integer. |
| `GET /nuget/{feed}/package/{id}/{version}` | Download by normalised version. |
| `PUT /nuget/{feed}` and `PUT /nuget/{feed}/package` | Push, multipart or raw, `X-NuGet-ApiKey` or Basic. |
| `DELETE /nuget/{feed}/{id}/{version}` and `…/package/{id}/{version}` | Unlist or hard delete per feed. |

Decisions this implementation makes, all visible to clients:

- **Latest flags come from the merged version list**, never from a per-source view, so exactly one entry
  carries `IsLatestVersion` and one carries `IsAbsoluteLatestVersion`. This is where FiGet deliberately
  differs from the recorded reference answers on proxy feeds.
- **Unparsed `$filter` or `$orderby` is a 400 naming the expression**, logged at Warning. An empty 200 would
  be indistinguishable from "the package does not exist", which is the git forges' failure mode.
- **Version properties compare as NuGet versions**, so `NormalizedVersion ge '1.0.0' and le '1.1.0'` and
  `$orderby=NormalizedVersion desc` order 1.10.0 after 1.9.0 rather than before it.
- **Default order**: `FindPackagesById()` ascending by version, as the reference server returned;
  `Search()` and `Packages()` id ascending then version descending.
- **`$top` is capped at 1000** and a `next` link is emitted, which is how PSResourceGet's request for 6000
  is answered.
- **Unlisted versions** report `Listed` false and `Published` 1900-01-01, as nuget.org does.

## Not yet recorded

- PSResourceGet in v2 mode against a server that does serve `/api/v2` (the PowerShell Gallery itself).
- `Update-Module` of a module with more than 40 versions after an older one was installed.
- Authenticated feeds (anonymous read was on for every feed).
