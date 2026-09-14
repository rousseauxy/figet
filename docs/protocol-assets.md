# Asset directories in FiGet

Implementation notes for `/endpoints/{directory}`. An asset directory is a feed of kind `Assets`: files by
path, downloaded by plain `GET`, so that an Ansible `win_get_url` task or an `Invoke-WebRequest` line keeps
working when its host name changes and nothing else does.

## Where the contract comes from

Three sources, in this order of trust:

1. **Recorded from the reference server** (2026-09-13), anonymous reads against an empty
   directory. These shapes are asserted by tests.
2. **The reference client library**, the reference server's open-source command-line client. Its model
   files open with a note that they *are* the specification of the JSON, so field names and the upload verbs
   are taken from there.
3. **The reference server's published API documentation**, for status codes and the rules it states in words.
4. **The reference client run against FiGet**: the reference client (2.4.2), every asset command it has (below). Where it
   disagreed with the first three, it won, because it is what people will point at this server.

Writing to the reference server to record uploads was not possible in this session, so every write-side
status below comes from sources 2 and 3, not from a recording. Where those sources are silent, the choice
is marked as FiGet's own.

## Routes

| Method | Path | Scope | Answer |
|---|---|---|---|
| GET, HEAD | `/content/{path}` | Read | The file. `Range`, `If-None-Match` and `If-Modified-Since` honoured; `ETag` is the quoted SHA-256. |
| PUT | `/content/{path}` | Push | Stores only when nothing is at the path. 201; **409** when a file is there *(status is FiGet's choice)*. |
| POST | `/content/{path}` | Push | Stores, replacing a file that is there. 201. This is what the reference client's asset upload sends. |
| PATCH | `/content/{path}` | Push | Replaces only a file that is there. 201; 404 when there is none. |
| DELETE | `/content/{path}` | Delete | 200. Also 200 when the file does not exist (documented as not an error). 400 for a folder. |
| GET | `/dir/{path}?recursive=` | Read | JSON array of items. A folder that does not exist is `[]` with 200 (recorded and documented). |
| POST | `/dir/{path}` | Push | Creates the folder and those above it. 201, also when it already exists. |
| POST | `/delete/{path}?recursive=` | Delete | 200. An empty folder goes without `recursive`; a full one answers 400 unless `recursive=true`. |
| GET | `/metadata/{path}` | Read | One item as JSON, or 404 `Asset not found.` as plain text (recorded). |
| POST | `/metadata/{path}` | Push | Sets the content type, user metadata and cache header. 200. |
| POST | `/content/{path}?multipart=upload&id=&index=&offset=&totalSize=&partSize=&totalParts=` | Push | Stores one part. 200. Nothing is visible until completion. |
| POST | `/content/{path}?multipart=complete&id=` | Push | Joins the parts into the file, replacing one that is there. 200; 400 when parts are missing or do not line up. |
| PUT, POST, PATCH | `/content/{path}` with `X-Source-Url: {url}` | Push | The server fetches the URL and stores it, with the verb's usual rule about existing files. 201; 400 when refused; 502 when the remote fails. **FiGet's own**, not in the reference API. |
| POST | `/import/{path}?format=zip\|tgz&overwrite=` | Push | Unpacks an archive into the folder. 200 with `{"imported", "skipped", "failed": [...]}`; 413 past the import limit. |
| GET | `/export/{path}?format=zip\|tgz&recursive=` | Read | The folder as an archive, entries named relative to it. Without `recursive`, only the files directly inside. |

Folders above an uploaded file are created on the way. Something above the path being a file is a 400, and
so is uploading to a path where a folder is.

Two anonymous switches, not one. *Download without credentials* covers `GET /content/{path}` and a file's
`/metadata/{path}`; *list without credentials* covers `/dir/`, `/export/`, a folder's `/metadata/` and the browse page.
With the first on and the second off - the setting the device-management consumers get - a client that knows its
paths downloads, and a stranger learns nothing: `/dir/` and `/export/` answer 401 with a Basic challenge, a folder's
metadata is the same 404 as a wrong path.

A directory backed by a folder on the server (`FiGet:Feeds:N:Folder`) answers the same routes from the folder itself,
with these differences: an item has no hashes, its `ETag` is size and modified time, its type comes from its extension,
and every write - `PUT`, `POST`, `PATCH`, `DELETE`, `/dir/`, `/delete/`, `/import/`, the multipart calls, `/metadata/`
- answers 403 unless `FolderWrites` is on; `/metadata/` and multipart always do, since nothing can be stored beside
the files. Hidden and system files, `web.config`, `Thumbs.db`, `desktop.ini` and `~$` lock files are never listed or
served, and a link or junction leading out of the folder is a 404 like a missing file.

## Verified with the reference client

The reference client (2.4.2), against a local instance, token passed on the command line so no source was
configured. All sixteen commands exit 0: `assets upload` (one request), `assets upload --partsize=1` (a 3.5 MB
file in four parts, confirmed from the request log, downloaded back with an equal SHA-256), `assets list`,
`assets metadata set custom`, `assets metadata set cache`, `assets metadata get`, `assets folders create`,
`assets folders import` (zip, and the tgz exported below), `assets folders export` (zip and tgz, the tgz round
trip keeping every SHA-256), `assets download`, `assets delete` for a file and `--force` for a full folder.

It found two things every other source missed, both now covered by tests:

- **Its listing URL is malformed**: `dir/{path}?recursive=false)`, with a stray parenthesis. A strict boolean
  binding answered its every listing with 400. `recursive` and `overwrite` are read leniently.
- **User metadata is a plain string** unless it is also sent as a header, and only then an object
  `{"value", "includeInResponseHeader": true}`. Its reader accepts both, which is the evidence the server
  being replaced answers with the same mix. FiGet reads both and writes exactly that.

Two of its quirks are its own and need nothing from the server: `folders export` rejects an output named
`.tar.gz` (it checks the last extension only; `.tgz` works), and 2.4.2 has no `--recursive` for export.

## Recorded answers

| Request | Status | Body |
|---|---|---|
| `GET /endpoints/{directory}/dir/` and `/dir` (empty directory) | 200 `application/json` | `[]` |
| `GET /endpoints/{directory}/content/` (nothing there) | 404 `text/plain` | `The specified asset was not found.` |
| `GET /endpoints/{directory}/metadata/` (nothing there) | 404 `text/plain` | `Asset not found.` |

Multipart, import and export follow the documented parameter names and the reference client's use of them;
their write-side statuses were not recorded from the reference server either.

## An item

```json
{
  "name": "installer.exe",
  "parent": "tools/runtime",
  "size": 3000000,
  "type": "application/vnd.microsoft.portable-executable",
  "content": "https://host/endpoints/{directory}/content/tools/runtime/installer.exe",
  "created": "2026-09-13T08:45:20.3021259Z",
  "modified": "2026-09-13T08:45:20.3021259Z",
  "md5": "…", "sha1": "…", "sha256": "…", "sha512": "…",
  "userMetadata": { "owner": "platform", "contact": { "value": "team@example.org", "includeInResponseHeader": true } },
  "cacheHeader": { "type": "ttl", "value": "60" }
}
```

- `parent` is omitted in the root, as the client model states. `type` is `dir` for a folder; a folder has no
  `size`, `content` or hashes.
- Null fields are left out rather than written as `null`.
- Dates are UTC with a `Z`. The reference model says dates are "forgiving"; its own example carries an offset.

## Decisions

- **Paths are case-insensitive and keep the case they were first written with**, like feed names and package
  ids. The folder `Tools` and a later upload to `tools/x` are the same folder.
- **A path is refused** when a segment is `.` or `..`, contains a backslash or a control character, is only
  spaces, or is longer than 255 characters, or when the whole path is longer than 400 (sized to stay inside a
  SQL Server index key). Doubled slashes are collapsed. `Domain/Assets/AssetPath.cs` is the whole rule.
- **The bytes are stored under a random id**, never under the path someone chose
  (`files/assets/{directory}/{ab}/{id}`). A name therefore cannot escape the storage root, collide on a
  case-insensitive disk or be a name Windows refuses, and a replace writes the new file before the old one
  goes. The path, size, type and hashes live in the `AssetItems` table.
- **The content type** is the one the uploader sent, except `application/octet-stream`,
  `application/x-www-form-urlencoded` and `multipart/*`, where the file extension decides. curl labels
  `--data-binary` as a web form by default, so honouring it would serve an installer uploaded the documented
  way as a form.
- **Nothing served runs as this site.** Every download carries `X-Content-Type-Options: nosniff` and
  `Content-Security-Policy: default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; sandbox`, and only raster
  images, `text/plain` and `application/json` open in a browser; every other type, HTML, SVG and PDF included, comes
  with `Content-Disposition: attachment` under its own name. Files are served from the site's own origin with a
  type the uploader chose, so an HTML upload would otherwise be a page that runs with the cookie of whoever opens
  its link. Download clients ignore all three headers. (2026-09-14 review.)
- **Hashes are computed while the upload is stored**, in one pass. MD5 and SHA-1 are reported because clients
  compare against them; nothing on the server trusts them.
- **Cache header:** a `ttl` type with a whole number of seconds becomes `Cache-Control: public, max-age=N` on
  download. Other types are stored and reported back but not applied, because what they should send is not
  documented anywhere we could read.
- **Cache modes per folder**, set on the browse page (*Cache*): *inherit*, *no-store* (`Cache-Control: no-store,
  no-cache, must-revalidate`, `Pragma: no-cache`, `Expires: -1`) or *max-age N* (`Cache-Control: public, max-age=N`).
  The nearest folder above the file decides, the directory's root included; a file's own `ttl` metadata wins over
  both. Fixed modes rather than free-form headers, which could switch off the sandbox policy, `nosniff` and the
  attachment disposition every download carries. Stored in FiGet (`AssetCachePolicies`), never beside the files, so a
  folder-backed directory has them too. (2026-09-14, for the shared-folder consumers.)
- **A folder on the server can be the directory** (`FiGet:Feeds:N:Folder`): no copy and no index, so a file dropped on
  the share over SMB is served at once and a deleted one is gone. The root and every resolved path are checked to stay
  under it, and a reparse point on the way is refused, because a share is written by people and a link out of it is one
  `mklink` away. Writes are off unless `FolderWrites` says otherwise: the share's own permissions decide who writes,
  and a second way in needs its own reason. Writes that are on go through a temporary file in the target folder and a
  rename, so a half-written upload is never the file a client downloads. (2026-09-14.)
- **User metadata marked `includeInResponseHeader` is stored but not sent as a header.** The header name the
  reference server uses was not observable without write access, and guessing one would be a promise.
- **Writing never works anonymously**, whatever the directory's anonymous-read setting says. Tokens are the
  same tokens as for feeds; a token scoped to one feed may be scoped to one directory.
- **The API accepts tokens only, never the sign-in cookie.** The browse page uploads through
  `/admin/assets/{directory}/upload` instead, with the antiforgery token in a `RequestVerificationToken`
  header, and stores through the same code. An API that honoured the cookie could be made to upload by any
  page an admin had open.
- **Each surface sees only its own kind.** `/nuget/{name}` answers 404 for an asset directory and
  `/endpoints/{name}` answers 404 for a package feed, so a client pointed at the wrong one fails plainly
  instead of seeing an empty feed.
- **Limits:** `FiGet:Limits:MaxAssetSizeMB`, default 1024, separate from the package limit because installers
  are far larger than packages. The request body limit is raised per request, so Kestrel's 30 MB default
  does not refuse the upload before the handler runs. A multipart upload is held to the same limit on its
  announced total, refused on the first part rather than on completion.

### Multipart uploads

- **Parts are kept on shared storage** (`files/feeds/{directory key}/asset-uploads/{id}/`), not in local temp: behind a
  load balancer the parts of one upload reach different replicas.
- **The client's id is hashed** into the storage name, because the API promises nothing about its shape.
- **Completion checks the parts line up**: every index from 0 once, each starting where the last ended, adding
  up to the announced total. Otherwise it answers 400 and keeps the parts, so a retry can send what is missing.
- **A part sent again replaces the earlier copy**, which is what a client retrying after a timeout needs.
- **Abandoned uploads are swept hourly** once nothing was added for `FiGet:Assets:IncompleteUploadExpiry`
  (24 hours). Every replica sweeps; it is harmless, since an idle upload removed twice is removed once.

### Archives

- **Every entry goes through the ordinary upload code**, so an archive can store nothing an upload could not.
  An entry named `../x` is refused as an invalid path, not resolved; backslashes are separators (archives made
  on Windows use them); links, devices and other non-file tar entries are skipped and reported as failed.
- **Without `overwrite` an existing file is skipped**, as documented; with it the file is replaced.
- **Not atomic.** Entries before a failure stay. The response says what was imported, skipped and failed.
- **The limit is on bytes actually unpacked** (`FiGet:Limits:MaxImportSizeMB`, default 4096), not on the sizes
  an archive declares, which is what stops a small archive that unpacks to a full disk. Past it the import
  stops with 413, keeping what came before.
- **A zip is spooled to a temporary file** first, because its directory is at the end; a tgz is read straight
  off the request.
- **An export is built in a temporary file** and then sent: the archive writers are synchronous in places, the
  server refuses synchronous response writes, and a finished file can carry its length and be resumed.

### Fetching from a URL

Not in the reference API. It exists to pin a vendor installer without downloading it to a workstation first,
and it makes the server send requests on a token holder's behalf - the shape of a server-side request forgery,
since what it fetches is stored and then readable. So:

- **Only http and https.**
- **The address is checked at connect time**, for the address actually connected to, on every connection a
  redirect opens as well. A host name checked up front could resolve to something else a moment later.
- **Private, loopback and carrier-grade NAT addresses are refused** unless
  `FiGet:Assets:RemoteFetch:AllowPrivateNetworks` is on. **Link-local addresses are refused always** - that is
  where cloud metadata services hand out credentials - including when reached through an IPv6 form that
  embeds them (IPv4-mapped, NAT64 `64:ff9b::/96`, 6to4 `2002::/16`).
- **A name resolving to one allowed and one refused address is refused**, rather than connecting to whichever
  answers first.
- **No proxy is used**, because through a proxy the check would see the proxy's address. An instance that can
  only reach the internet through a proxy cannot fetch by URL yet (backlog).
- **The size limit applies while downloading**, and the whole fetch, body included, is bounded by
  `FiGet:Assets:RemoteFetch:Timeout` (30 minutes).
- **The page's form reports a fixed code** (`?fetch=refused` and so on) mapped to text in the page, so nothing
  from the address bar is ever shown back as a message.
