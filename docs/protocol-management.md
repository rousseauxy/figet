# Package management API in FiGet

Implementation notes for `/api/packages/{feed}`, the part of the replaced server's management API that scripts
call to see what a feed holds, find the latest version, and clean up. It sits beside the NuGet protocols
rather than replacing them: clients install through `/nuget/{feed}`, scripts manage through this.

## Where the contract comes from

1. **The reference client**, the reference server's open-source command-line client: its client class builds these URLs and its model
   files (`PackageVersionInfo`, `PackageStatus`, `BasicFeedInfo`) describe the JSON.
2. **The scripts that exist**, read for which fields they use: a CI version fallback reads `versions` and sorts
   on `published`, taking `version`; a clean-up script reads `latest` and `versions`, filters on `publishedBy`,
   and posts `delete` for everything but the latest.
3. **The reference client run against FiGet** (below), which found a call the first two did not show.

The build plan's own sketch (§4.5) had `latest` answer a single object. The client reads a list, and so does
FiGet.

## Routes

| Method | Path | Scope | Answer |
|---|---|---|---|
| GET | `` (the feed itself) | Read | `{"id", "name", "feedType": "nuget", "packageType": "nuget"}` |
| GET | `/versions?name=&version=` | Read | Every stored version, newest first; `name` and `version` narrow it. `[]` when nothing matches. |
| GET | `/latest?name=&stableOnly=` | Read | A list with one entry per package: the highest listed version, or the highest listed stable one. |
| GET | `/download?name=&version=` | Read | The `.nupkg`. `version` may also be `latest` (the version `/latest?stableOnly=true` reports) or `latest-unstable` (the highest listed, prerelease included). 404 when the feed does not store that version. |
| POST | `/delete?name=&version=` | Delete | 200; 404 when not found. |
| POST | `/status?name=&version=` | Delete | Body `{"listed": true\|false}`. 200. |
| PUT, POST | `/upload`, `/upload/{fileName}` | Push | The body is the package. 201; 409 when the version exists and the feed does not allow overwrite. |

Credentials as everywhere else: `X-ApiKey` (what the scripts send), `X-NuGet-ApiKey`, Basic or Bearer. Reads
follow the feed's anonymous-read setting.

## A version

```json
{
  "purl": "pkg:nuget/Newtonsoft.Json@13.0.3",
  "name": "Newtonsoft.Json",
  "version": "13.0.3",
  "totalDownloads": 12,
  "downloads": 4,
  "published": "2026-09-13T10:41:07.12Z",
  "publishedBy": "SYSTEM",
  "size": 2439485,
  "listed": true,
  "sha512": "…",
  "deprecated": false
}
```

## Decisions

- **Stored versions only.** On a proxy feed, `versions` and `latest` list what was pushed and what was cached,
  not what only the upstream offers. That is what the replaced server does, and what a clean-up script is
  about: what this server holds. The NuGet endpoints remain the place for the merged view.
- **Unlisted versions are listed**, with `listed: false`. A clean-up script needs to see them most of all.
- **`latest` uses the one version-list rule** every NuGet listing uses, so it cannot name a different latest
  version than a client would install.
- **`publishedBy`** is `SYSTEM` for a version cached from an upstream, which is how the reference server labels
  one; left out for a pushed version, because FiGet does not record who pushed. (A production server was seen
  to label cached versions `Anonymous` instead, so a script filtering on the exact word needs checking.)
- **Hashes:** only `sha512`, the hash NuGet itself uses, converted to hexadecimal. MD5, SHA-1 and SHA-256 are
  not computed for packages and are left out.
- **`delete` removes the version and its file**, whatever the feed's NuGet delete setting says: that setting may
  only unlist, and this API has a separate call for listing, so a delete here is a delete.
- **`status`** can list and unlist. A download override (`allow`) and deprecation have nothing in FiGet to act on;
  a request setting either is refused with 400 rather than accepted and ignored.
- **Not implemented:** `metadata`, `audit`, `promote`, `repackage`. Promotion between feeds is on the backlog.
- **An asset directory is not a package feed** here either: every route answers 404 for one.

## Verified with the reference client

The reference client (2.4.2) against a local instance, with two real packages (Newtonsoft.Json 13.0.1 and 13.0.3): `packages
upload` twice, `versions`, `list` (which is `latest`), `download` (byte-identical to the upload), `status
unlisted` (the latest moves to 13.0.1) and back to listed, and `delete`; all exit 0. `status deprecated` is
refused with the 400 above, as intended.

The first run failed `download` and `delete` with 404 before either request was sent: the client first asks
`GET /api/packages/{feed}` what kind of feed it is, to decide whether packages take qualifiers. That route now
exists, with a test.

The scripts' own reading of the answers was replayed too: `versions` sorted on `published` descending picks
13.0.3 and casts to `[version]`, and `latest` read as `.Version` gives 13.0.3.
