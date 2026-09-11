# NuGet v3 in FiGet

Implementation notes for the v3 surface. The shapes follow build plan §4.2; this file records what the
real clients were observed to do, and the decisions taken where the documentation is silent.

## Routes

All under `/nuget/{feed}/v3`, plus the symbol server.

| Method | Path | Resource |
|---|---|---|
| GET | `/index.json` | service index |
| GET | `/registration/{id}/index.json` | registration index |
| GET | `/registration/{id}/page/{lower}/{upper}.json` | registration page (only referenced when not inlined) |
| GET | `/registration/{id}/{version}.json` | registration leaf |
| GET | `/flatcontainer/{id}/index.json` | version list |
| GET | `/flatcontainer/{id}/{version}/{id}.{version}.nupkg` | package |
| GET | `/flatcontainer/{id}/{version}/{id}.nuspec` | nuspec |
| GET | `/query` | search |
| GET | `/autocomplete` | autocomplete |
| PUT | `/publish` (and `/publish/`) | push |
| DELETE | `/publish/{id}/{version}` | unlist or hard delete, per feed |
| POST | `/publish/{id}/{version}` | relist |
| PUT | `/symbolpublish` | symbol package push |
| GET | `/nuget/{feed}/symbols/{file}/{key}/{file}` | symbol server |

## Observed client behaviour

- **`dotnet nuget push` and nuget.exe PUT to the `PackagePublish` URL with a trailing slash**
  (`…/v3/publish/`), as multipart/form-data with one file part. When a `.snupkg` sits next to the
  `.nupkg`, the client pushes it to `SymbolPackagePublish` in the same command.
- **The server's message on 409 is shown to the user** when it is the HTTP reason phrase:
  `409 (Smoke.Library 1.0.0 already exists in feed 'modules'.)`. FiGet sets the reason phrase on every
  push outcome.
- **`dotnet add package` reads the registration index, then the flat container** version list, then the
  nupkg. Restore uses the flat container only.
- **PSResourceGet** detects v3 from the `/v3/index.json` suffix, finds by name through the registration
  index, and refuses wildcard names and tag searches for every v3 repository before any request.
- **nuget.exe `list`** refuses v3 sources; `search` works.
- **The NuGet 7 client refuses plain-HTTP sources** for push unless the source entry in `nuget.config`
  sets `allowInsecureConnections="true"`. The library applies this even when called programmatically and
  reports it only through its logger.
- **PackageManagement's NuGet provider 2.8.5.208** (the version on Windows PowerShell 5.1 fleets) has no v3
  client. See `docs/status.md`.

## Decisions

- **Versions in URLs** are the lower-cased normalised version. A request using another spelling of the
  same version (`1.2.3.0` for `1.2.3`) is normalised and served.
- **Registration inlining**: pages of 64 leaves; inlined when the package has at most 128 versions,
  otherwise each page is a separate document. PackageManagement's later provider and the NuGet client
  both follow non-inlined pages.
- **Unlisted versions** appear in the registration with `listed: false` and `published` set to
  `1900-01-01T00:00:00+00:00`, as on nuget.org; they stay in the flat container and are downloadable;
  search and autocomplete omit them.
- **SemVer 2.0.0**: search and autocomplete hide SemVer 2 versions unless `semVerLevel=2.0.0` is sent, and
  compute "latest" over what remains. Registration and flat container always include them.
- **Search query syntax**: free text matches id, title, tags, summary, description and authors;
  `packageid:` is an exact id match; `id:` a contains match; `tags:`/`tag:` an exact tag token; `*` is a
  wildcard in any value. All terms must match. An exact id match on the first term ranks first, then
  ids in order.
- **`totalDownloads` and per-version `downloads`** count successful nupkg downloads from the flat container.
- **Symbols**: only portable PDBs are accepted; the key is the 32 hex digits of the PDB id followed by
  `ffffffff`, matched case-insensitively. A symbol package for a version that does not exist is 404; a
  symbol package pushed to the package endpoint is 400.
