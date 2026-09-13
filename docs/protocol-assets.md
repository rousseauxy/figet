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

Folders above an uploaded file are created on the way. Something above the path being a file is a 400, and
so is uploading to a path where a folder is.

Not implemented, deliberately, and listed in `docs/backlog.md`: multipart upload (`?multipart=`), archive
import and export (`/import`, `/export`), and fetching a file from a remote URL.

## Recorded answers

| Request | Status | Body |
|---|---|---|
| `GET /endpoints/{directory}/dir/` and `/dir` (empty directory) | 200 `application/json` | `[]` |
| `GET /endpoints/{directory}/content/` (nothing there) | 404 `text/plain` | `The specified asset was not found.` |
| `GET /endpoints/{directory}/metadata/` (nothing there) | 404 `text/plain` | `Asset not found.` |

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
  "userMetadata": { "owner": { "value": "platform", "includeInResponseHeader": false } },
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
- **Hashes are computed while the upload is stored**, in one pass. MD5 and SHA-1 are reported because clients
  compare against them; nothing on the server trusts them.
- **Cache header:** a `ttl` type with a whole number of seconds becomes `Cache-Control: public, max-age=N` on
  download. Other types are stored and reported back but not applied, because what they should send is not
  documented anywhere we could read.
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
  does not refuse the upload before the handler runs.
