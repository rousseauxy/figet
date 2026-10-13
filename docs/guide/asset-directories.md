# Asset directories

An asset directory holds files by path: installers, archives, scripts, anything a machine downloads by name. A file is
fetched with a plain `GET` on `/endpoints/{directory}/content/{path}`, with `Range` for a resumed download and `ETag`
for a cache that already has it, so `curl`, `Invoke-WebRequest` or an Ansible `win_get_url` task is all a client needs.

Every file downloads, whatever its type: a directory is a store of assets a machine fetches, not a viewer for
documents or pictures. Opening one from the browse page saves it rather than showing it, and nothing uploaded can
run as a page of this site.

## Two kinds of content

| | FiGet's own storage | A folder on the server |
|---|---|---|
| Where the files are | On FiGet's storage volume, one record per file | In the folder itself, as it is: nothing copied, nothing indexed |
| How files get in | Drag and drop in the browser, an authenticated upload (multipart for very large files), a zip or tar.gz unpacked into a folder, or fetched by the server from a URL | Written to the folder directly, over SMB, by an application or by a person; through FiGet only when its writes are switched on |
| Hashes | MD5, SHA-1, SHA-256 and SHA-512, recorded at upload | None: the ETag comes from the size and modified time |
| Fits | A curated set of files people upload | A share that applications and people already write to, published for download |

A file placed in a folder-backed directory is served at once, and a file deleted from it is gone.

### A folder on the server

There are two ways to give a directory a folder, one for each person who decides:

- **Chosen on the pages.** The operator mounts the shares under one folder, set as `FiGet:Assets:SharesRoot`, one
  sub-folder per share. An administrator then picks a sub-folder by name: under *Content* on the create form, or in the
  *Content* section of the directory's settings page, which also moves a directory onto a folder, between folders, or
  back to FiGet's own storage. Moving a directory off a folder never touches the folder.
- **Set in configuration.** `FiGet:Feeds:N:Folder` names any path for a directory, and is applied again at every start.
  A directory with a folder from configuration shows no chooser.

The pages offer names and never take a path, so a page cannot publish a folder the operator did not mount. Only real
sub-folders are offered: no links, nothing hidden or system.

A compose example with one share:

```yaml
services:
  figet:
    environment:
      FiGet__Assets__SharesRoot: /shares
    volumes:
      - /srv/shares/scripts:/shares/scripts:ro
```

On Kubernetes or OpenShift the same folder is a ReadWriteMany volume mounted under the shares root.

Some things in a folder are never listed or served: hidden and system files, `web.config`, `Thumbs.db`, `desktop.ini`,
Office `~$` lock files, and any link that leads out of the folder. In a Linux container a folder counts as hidden when its
name starts with a dot.

**Writes through FiGet are off by default** for a folder: the share's own permissions decide who writes. Switched on,
uploads, new folders and deletes act on the folder, and an upload is written to a temporary file and renamed into place,
so a client never downloads a half-written file.

## Who may download, and who may list

Two switches per directory:

- **Anyone can download a file without a token.** Whoever has a file's exact address gets the file.
- **Anyone can list and browse the folders without a token.**

A consumer that knows the paths it fetches needs only the first. With listing off, a folder's address and a wrong path
answer the same 404, so there is nothing to discover, and listing needs a signed-in account or a key with Read.

A consumer can also get a key of its own: a read-only key limited to that one directory, sent as an `X-ApiKey` header or
as the password of Basic authentication. That gives a way to revoke one consumer and a name on its downloads in the
logs. Writing never works without credentials, whatever the switches say.

## Allowed networks

A directory's settings page takes a list of addresses and ranges (`10.0.0.0/8`, `2001:db8::/32`). A request from anywhere
else is refused with 403, whatever key or account it carries. Behind a reverse proxy the address is the one the proxy
forwards (see [Running in production](running-in-production.md)).

## A download link that expires

Sometimes one file has to reach a machine or a person that has no key and no account, and opening the directory to
everyone for it is too much. A download link does that for one file: an address that downloads it without credentials,
until the link ends.

**Who makes one.** An account or a key that may *publish* to the directory. Reading is not enough: a link hands the
file to someone the directory does not know, and that is a decision about what the directory gives out.

**On the browse page**, a file's row has a **Link** button. It makes a link that lives for the default time and shows it
with a copy button.

**From a script**, with the lifetime in hours:

```powershell
$link = Invoke-RestMethod -Method Post "https://packages.example/endpoints/installers/link/agents/setup.msi?hours=4" `
    -Headers @{ 'X-ApiKey' = $key }
$link.url          # https://packages.example/share/CfDJ8...
$link.expiresUtc   # when it stops working
```

Whoever has the link has the file, so treat it like the file itself. What limits it:

- **It ends.** After the lifetime it was made with, at most a week unless the server is configured otherwise.
- **It is for the file as it was.** Replace the file and the link stops working; it was given for the old content, not
  for whatever is at that path later. Delete the file, or the directory, and it stops too.
- **Allowed networks still apply.** A link does not move where a directory may be reached from.
- **It says nothing by itself.** The address names no directory and no path, and every link that does not open a file
  gives the same answer, whether it expired, was mistyped or never existed.

One link cannot be taken back on its own: nothing is stored per link, which is also why every replica honours a link
any of them made. To end one early, replace or delete the file. To end all of them, an operator sets
`FiGet:Assets:ShareLinks:MaxHours` to `0`, which also switches the feature off.

Making a link and each use of one are in the audit log (`asset.link.create`, `asset.link.use`), the use with the
address it came from. The log names a link by a short fingerprint and never holds the link itself.

## Cache modes per folder

Set on the browse page, per folder:

| Mode | Sends |
|---|---|
| Inherit | What the folder above says; the directory's root defaults to no cache header |
| No store | `Cache-Control: no-store, no-cache, must-revalidate`, `Pragma: no-cache`, `Expires: -1` |
| Max age | `Cache-Control: public, max-age=N` |

The nearest folder above a file decides, and a file's own cache metadata wins over its folders. The modes are a fixed set
rather than free-form headers, so no setting can switch off the protective headers every download carries.

## What a download carries

Every download is served so that nothing in it can run as part of the site: `X-Content-Type-Options: nosniff`, a
sandboxing content security policy, and `Content-Disposition: attachment` for every type except raster images, plain
text and JSON. Download clients ignore these headers. The one visible difference is that a browser saves an HTML or
PDF file instead of opening it.
