# Backlog

What is not built yet, and why it is not built yet. `docs/status.md` is the opposite of this file: it
records what *was* done, dated, with the evidence. Nothing is listed here without a reason it is not
simply done now — either it depends on something that does not exist, or it is a decision nobody has
taken.

Ordered roughly by when it is likely to be worth doing, not by importance.

## Next

### ~~Asset directories backed by a shared folder~~ (designed and built 2026-09-14)

Built as designed below - `docs/status.md`, "Asset directories backed by a shared folder" - with the folder from
configuration, the two anonymous switches on every directory, writes off by default, and cache modes per folder. Since
2026-09-15 an administrator can also pick the folder on the pages, from the sub-folders of one configured mount
(`FiGet:Assets:SharesRoot`). What stays open is the consumer questions under *Open before building*, which only the
consumers answer, and three smaller things: not yet tried against a real SMB mount from a pod (the tests use folders on
the test host); only direct sub-folders of the mount are offered, so a share that must be nested needs a mount of its
own; and the hidden-attribute exclusion is Windows-only, on Linux a folder hides by its dot.

**Why.** Folders on a file share are published today through a web server's virtual folders, so that applications
can download from them: device-management scripts, a CRM's assets, network appliances. The same folders are written
directly over SMB, by applications and by people dropping files in. Replacing the web server with FiGet needs an asset
directory whose content *is* that folder. A normal asset directory cannot do this: FiGet stores its files under random
ids with a database row each, so a file placed on the share would not exist for it.

**What the consumers need, as the owner described them.**
- Download only. Every consumer knows the exact paths it fetches; none lists a folder. Browsing is for people, and is
  normally off.
- No credentials: today anyone with a file's URL downloads it, and a folder shows nothing (browsing off, or an empty
  index page).
- The URL becomes FiGet's own (`/endpoints/{dir}/content/{path}`); no path alias.
- No-cache on some folders: `Cache-Control: no-store, no-cache, must-revalidate`, `Pragma: no-cache`, `Expires: -1`.
- No HTML directory listing, and no Windows authentication.

**Design.**
1. **A folder-backed directory kind.** Its root is a mounted path from configuration, never typed in the UI: the
   operator provides the mount. On the cluster that is an SMB/DFS share mounted into the pods with a service account
   from ESO, the arrangement the reference server's shared storage already uses.
   - Downloads and listings read the folder itself. No copy, no index: a file placed on the share is served at once,
     and a deleted one is gone.
   - The ETag is size plus modified time. A SHA-256 is computed only when asked for.
   - Every resolved path must stay under the root; symlinks and junctions leading out of it are refused.
   - Never listed or served: `web.config`, `Thumbs.db`, `desktop.ini`, `~$` lock files, hidden and system files.
2. **Writes through FiGet are off by default for this kind.** The share's own permissions decide who writes, and a
   second way in would need its own reason. When turned on, uploads, folders and deletes act on the share, with the same
   Publish and key rules as other directories.
3. **Two access switches instead of one "anonymous read"**, for every asset directory:
   - *download without credentials*, and
   - *list without credentials*.

   These consumers get download on, list off: exactly today's behaviour. A folder URL or a wrong path answers the same
   404, so nothing can be discovered, and listing needs a signed-in account or a key with Read.
4. **Optional per-consumer keys**, which exist already: a read-only key limited to the directory, sent as `X-ApiKey` or
   as the Basic password. That buys revocation and a name on every download in the logs, for any consumer that can send
   a header or Basic credentials; the others keep downloading without.
5. **Cache modes per directory and per folder, inherited downward**: default, no-store (the three headers above), and
   max-age N. Fixed modes, not free-form headers, which could switch off the sandbox policy, `nosniff` and attachment
   downloads every asset response carries. Stored in FiGet, since nothing can be written beside the files.

**Open before building.**
- Which consumers can send a header or Basic credentials (scripts can; appliances and the CRM are to be checked).
- Where the device-management scripts download from: outside the network means the directory is reachable from the
  internet.
- Many devices behind one address count against the anonymous rate limit (1,200 a minute by default); raise it, or give
  that consumer a key.
- Half-written files: a large copy onto the share is visible while it is written, as it is today. Treat as unchanged
  unless it turns out to matter.
- Scale: listing straight from the share is fine for thousands of files; a very large tree would need an index.

**Size.** Three to four days with tests on both databases, tried against a Samba share on the test host.

### Scan uploads for malware (designed 2026-09-14)

**Why.** Anyone with Publish on an asset directory or a feed can upload a file, and installers are exactly what
malware pretends to be. Since the review nothing served from a directory can run as the site, and every write is
audited by name; what is missing is a check on the bytes themselves. The files must never leave the network, which
rules out any service that takes the file: what is sent is the file, so the scanner has to be ours.

**Design.**
1. **A port, `IContentScanner`**, with one call: scan a stream or a path, answer clean, infected with the signature
   name, or unavailable. One adapter, for ClamAV's daemon over its own protocol (`INSTREAM` for a stream, `SCAN` for a
   path the daemon can see); the protocol is small enough to write by hand, about forty lines, so no package.
2. **The engine runs beside FiGet, never inside it.** ClamAV is a C library with a signature set of several hundred
   megabytes that changes daily and one to two gigabytes of memory while loaded; in the process it would tie our
   restarts, memory and updates to it, and there is no maintained .NET binding. On the cluster it is a **sidecar
   container in the FiGet pod**: localhost or a Unix socket, so it is reachable from nowhere else, the same volume
   mounted so large files are scanned by path, and one Deployment in the chart. Stand-alone it is a second, optional
   service in `compose.example.yml`. FiGet only knows an address.
3. **Every write path scans before its row exists**, which is where each already stages its bytes: an asset upload, a
   multipart completion, each entry of an archive import, a fetch by URL, a package push after its upload buffer, and a
   copy cached from an upstream. A hit refuses the write with a plain reason, stores nothing, and writes an audit entry
   (`asset.refused`, `package.refused`) with the file, the signature and who sent it.
4. **Off unless configured** (`FiGet:Scanning:ClamdAddress`). When configured and the daemon is down, writes are
   refused and the reason logged, never let through; reads are never affected.
5. **Large files.** The daemon's stream limit defaults to 25 MB and its scan size to 100 MB. Installers go by path on the
   shared volume, with `MaxFileSize` and `MaxScanSize` raised in its configuration; a 400 MB scan takes seconds to a
   minute inside the upload request, which the fetch path already allows for.
6. **Signatures** need `freshclam` to reach a mirror, or an internal mirror: one egress rule on the cluster.

**What the reference server does.** Nothing of this kind, checked 2026-09-14. It scans no file contents: known malicious
*packages* are treated as vulnerabilities, matched by package identity against a database it downloads nightly, and
its answer on a forum is that by the time a package is known to be malicious the public gallery has usually removed
it. Asset directories get no scanning at all. Its installation guide goes further and tells operators that its storage
and processes must not be scanned, filtered or quarantined by antivirus, EDR or file-integrity tools, because they
slow its file-heavy work and mistake ordinary package contents - libraries, scripts, executables - for threats. Two
consequences for the design above: scan at write time inside the request and never let an on-access scanner near the
storage volume, and expect false positives on installers and scripts, so a scan hit must name its signature in the
audit entry and a directory must be able to turn scanning off.

**What it does not buy.** Known malware only. A vendor installer tampered with upstream, or a bespoke tool, passes. The
next step in that direction is verifying Authenticode signatures on `.exe` and `.msi` uploads and showing the signer
on the file's row; a hash lookup against a reputation service (the SHA-256 leaves, the file does not) is a possible
second opinion, weak for internal files.

**Open before building.**
- Whether the organisation already runs a scanner with an ICAP interface, which most enterprise products expose for
  proxies; if so, an ICAP adapter behind the same port is half a day and uses the engine the security team maintains.
- Whether shared-folder directories (above) should scan at all: the file server's own antivirus already scans what
  lands on the share.
- Memory request for the sidecar on the cluster, and where `freshclam` may fetch from.

**Size.** One to two days: the port and adapter, the six hooks, a stub scanner in the tests, and the EICAR string
against the real daemon as a CI service container. The sidecar and compose service come with the phase 6 chart.

### Find-Module is slow for a package with thousands of versions

`Find-Module PnP.PowerShell` took 44s over v2 where `Find-PSResource` took 3.1s over v3 (2026-09-12).

Measured 2026-09-13 (docs/status.md, "Find-Module on a package with thousands of versions"): not the merge,
not `$skip`. A page costs what its response size costs, and tags are 92-96% of every response - about 80 MB
for one `Find-Module`. Gzip shrinks the wire size 88% but not the time, so the cost is building and writing
the entries. Options, for a decision:

1. **Profile the writer first** (no behaviour change). Find out whether the time is the Atom writer, the row
   building or the string handling, and make that part cheaper. Safe; the size of the win is unknown.
2. ~~**Enable response compression** for `/nuget` regardless.~~ Done 2026-09-13
   (`FiGet:CompressProtocolResponses`, on by default). Does not fix the time measured on a fast link.
3. **Trim tags on older versions only** (keep them on the latest few). Cuts the payload most, but
   `Find-Module -AllVersions` would show no `Includes` for old versions. User-visible.
4. **Accept it.** The client asks for every version by design; PSResourceGet over v3 is already fast.

### Watch the PSResourceGet fix (comment posted 2026-09-13)

Not a change to this server. PSResourceGet chooses a download URL by substring match on the version, so a
requested version that is a text prefix of a longer one installs the wrong package (docs/status.md, "An
install that fetched the wrong version"). It is open as PowerShell/PSResourceGet #1657, with an unmerged
fix in PR #2019.

Our reproduction was posted on the PR on 2026-09-13
(https://github.com/PowerShell/PSResourceGet/pull/2019#issuecomment-5653133353; text in
docs/upstream/psresourceget-1657-comment.md). It also reports a collision the PR still lets through: its
file-name check is a suffix match, so requesting 2.5.1 selects 3.2.5.1. Checked by compiling the PR's
matcher, along with the suggested full-name comparison. Nothing left to do here but watch the PR; when a
PSResourceGet release carries the fix, re-run the 2.2.4 / 2.2.5 saves over v3 and close this entry.

## Soon

- **Record the asset write side from the reference server.** Its uploads, deletes and metadata were taken
  from the client library and the documentation, because writing to the reference instance was not possible
  in the session that built them. Still unconfirmed: the status of a `PUT` onto an existing file (FiGet
  answers 409), the header name for user metadata marked `includeInResponseHeader` (FiGet sends none), and the
  body of an import response (FiGet answers counts). The reference client has since run every asset command
  against FiGet, which settled the metadata shape. Needs an API key for the reference instance.
- ~~**Drive the upload drop zone in a real browser.**~~ Done 2026-09-14 by the owner on the live instance (docs/status.md,
  "The drop zone, clicked through"): single and multiple drops, a 400 MB file, the replace question, an archive import
  with and without replacing. Two things it found are fixed in the same entry.


### When the repository goes public: split validation from publishing

Raised 2026-09-12, to be decided when we get there rather than now.

Today `ci.yml` runs on push to `main` and on pull requests, with two jobs: `build-and-test` (both
database providers, the migration check, the whole suite) and `container`, which builds the image, loads
it, runs it under an arbitrary UID and curls `/health/ready`. It never publishes anything, because the
house rule is no images while the repository is private.

The proposal was to run only on a tag, as the sibling application does. Worth writing down what that
application actually does, because it is not quite that: `build.yml` triggers on `pull_request` and
`workflow_dispatch` and only validates, while `docker.yml` triggers on `push: tags: docker-*` and is the
release path. **The tag gates publishing, not testing** — and `build.yml`'s own comment records why the
pull-request trigger exists at all: *"a broken main was invisible until somebody tagged."*

The trap in copying it literally: **this repository has no pull requests.** Everything goes straight to
main, so a pull-request-only trigger would mean the suite never runs and main goes unvalidated — exactly
the failure that comment describes. The push-to-main trigger is currently the only safety net there is.

Cost does not argue either way: public repositories get unlimited Actions minutes against 2,000 a month
while private, so going public removes the pressure rather than creating it.

Shape to decide on: keep `build-and-test` on push to main; keep the container smoke test there too, since
it has already caught a real defect (a project added without its `COPY` line in the Dockerfile's restore
layer); and add a *separate* tag-triggered job that pushes to GHCR once the repository is public.

### Pin the SDK and runtime base image tags

Both stages of `deploy/Dockerfile` float: `sdk:10.0` and `aspnet:10.0`. The sibling application pins both
(`sdk:10.0.302`, `aspnet:10.0.10`) with a comment giving two reasons — avoiding float drift, and keeping
the runtime on the same patch as the SDK that *composed* the static web assets it will serve.

To be clear about what this is not: the SDK was **not** the cause of the missing framework script on
2026-09-12. Both the container image and this machine resolved to 10.0.401 under our `global.json`, and
the cause was `--no-restore` on the publish. This is prophylactic, and the argument for it is the shape
of that failure rather than its cause: a build/runtime mismatch fails the same silent way — no error, a
healthy container, and a 404 for a file the page asks for. Two floating tags that happen to agree today
are not an arrangement that keeps agreeing.

Decide alongside it whether the runtime pin is worth the maintenance: pinning means noticing patch
releases by hand, and the sibling's comment records a case where the runtime image *was* the fix for a
set of CVEs in an assembly the app never ships itself.

### Phase 6: what the review left for the cluster deployment

From `docs/reviews/2026-09-14-architecture-security.md`, with the OpenShift admins' answers of the same day: secrets
come through ESO, the chart is Helm, storage may be S3 or a ReadWriteMany volume.

- **The Helm chart** must set `FiGet:PublicBaseUrl`, `Database:ExpectedReplicas`, `FiGet__DataProtection__MasterKey`
  from an ESO-synced Secret (FiGet refuses to start with more than one replica without it), upstream credentials as
  `FIGET_UPSTREAM_*` variables from the same kind of Secret, `Storage:TempPath` on the volume or an `emptyDir`, and a
  proxy body limit of at least `MaxAssetSizeMB`.
- **Trust only the ingress for forwarded headers** (S10.3): `ASPNETCORE_FORWARDEDHEADERS_ENABLED` trusts every peer, so
  either configure `KnownNetworks` to the ingress range or keep the pod reachable only through it with a NetworkPolicy.
- **Storage: start on a ReadWriteMany volume.** It needs nothing new: the file-system storage already writes
  atomically and is keyed by feed. S3 would need the storage ports to take a byte range (a download is served with
  Range from a seekable stream today) before an adapter; not worth doing unless the platform prefers S3.
- **Secrets stay environment variables.** ESO syncs into a Secret the pod reads as variables, which is what
  `CredentialRef` and the connection string already use; a separate secret-source port (mounted files) is not needed.

### Smaller items from the 2026-09-14 review

Each is Low, and none is reachable without an account that already has rights; ids refer to the review.

- ~~**S6.1, S6.3** Cap the nuspec entry read into memory at a few megabytes, and stream symbol PDBs to storage instead
  of holding each one whole.~~ Done 2026-09-14 (docs/status.md, "Four of the smaller review items").
- ~~**S6.4** Validate an asset's content type set through the metadata call.~~ Done 2026-09-14, same entry.
- **S9.2** A sweep or `figet verify` that lists files no row names: a failed delete or two concurrent replaces of an
  asset leave one.
- ~~**Name uniqueness across feeds and alternate names** is a check in the store, not an index: a `Names` table with the
  unique index, written by both paths.~~ Done 2026-09-14 (docs/status.md, "Names as one table").
- **Hash verification on v2 cache fill** (the v2 client library exposes the hash; v3 does not).
- **S1.3** Locked-out and disabled answers say a name exists after five attempts; **S7.2** rate limits are per replica;
  the two connector display divergences (review 3.4). ~~S5.1 reserved IPv4 ranges; the appearance page's "Reload packs"
  acts on one replica.~~ Done 2026-09-14, same entry.
- **A download attempt for an id no upstream lists** is still made per upstream. Kept on purpose: a package published a
  minute ago can be downloadable before a gallery's listing shows it, and the attempts are bounded by the rate limit.

## Later

- **History tab** on a version: the audit log exists now (filter by feed on its page); a per-version view of it is not built.
- **Usage per version.** Usage per feed is counted since 2026-09-14 (`FeedUsage`, the graph under the feed lists). What is
  not: which versions are used, and whether a download came from the cache or the upstream. That needs per-version
  records, heaviest by volume, and belongs apart from both the per-feed counts and the audit log.

## Decided against

- **A picture on the account** (Gravatar, suggested and dropped 2026-09-14). The owner sees no place it would be used, and
  a Gravatar is an image the browser fetches from gravatar.com by a hash of the account's email: every page with the menu
  bar would tell a third party which hashed addresses use this server, and show a broken image without internet.

- **Promotion between feeds** (dropped by the owner, 2026-09-13). The server being replaced has it; nothing in the
  fleet uses it, and pushing the same package to the other feed does the job.

- **Per-feed version filtering on proxy feeds.** A module whose newest release drops support for an older
  client is the module's compatibility problem; FiGet offers every upstream version and the client picks.
  Recorded in build plan section 9 so it is not re-litigated.
