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

### ~~Find-Module is slow for a package with thousands of versions~~ (profiled and fixed 2026-09-16)

Option 1 was taken, and it was neither the Atom writer nor the row building: the tags of every described
upstream version were lower-cased again on every request, 280 ms of a 290 ms page for PnP.PowerShell's 2,101
versions, paid on each of the 53 pages a `Find-Module` walks. They are kept per description now
(docs/status.md, "Find-Module profiled"). Nothing a client sees changed: same pages, same tags.

Left undone deliberately: options 3 (trimming tags on older versions) and 4. Neither is needed at the
measured speed.

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

### Findings from other package servers' issue trackers (cross-check of 2026-09-15)

The reference server's public tracker (2,316 issues, 542 read against the code), BaGet (446) and BaGetter (106) were
checked against FiGet: every issue that could concern a v2/v3 server, PowerShell clients, proxy feeds, assets or
authentication was judged against a file and line. 256 are already handled, with evidence; about 1,190 concern things
FiGet does not have. What applies is below, in this section and under *Soon* and *Later*.

- ~~**Tags longer than 4,000 characters on SQL Server.**~~ Done 2026-09-15 (docs/status.md, "Two findings from the issue
  cross-check"). A PowerShell Gallery module lists each exported command as a tag, and real modules carry 15,000 to 20,000
  characters; SQL Server refused the insert and the store answered it as "already exists" (BaGet #273, #590, #609).
- ~~**Search flagged a cached older version as latest on a proxy feed.**~~ Done 2026-09-15, same entry. The merged version
  list now covers `Search()`, `/v3/query` and autocomplete.
- ~~**The v2 search read a fixed window of 2,000 packages in memory.**~~ Done 2026-09-15 (docs/status.md, "The rest of the
  cross-check's Next items"). A listing ordered by id - every recorded client search - is read a chunk at a time with no
  cap, and id and tag predicates in the filter narrow it in the database. Other orders keep the window and log when they
  reach it. The reference server's tracker shows seven releases of slow or timed-out latest-version queries.
- ~~**A copy cached by a pinned download was stored as listed when the upstream hides that version.**~~ Done 2026-09-15, same
  entry.
- ~~**A storage failure while caching answered 500.**~~ Done 2026-09-15, same entry: 503 with `Retry-After`, nothing stored.
  Serving the downloaded bytes uncached was not done: a disk that cannot take the package is better reported than hidden.
- ~~**A version row whose file is missing was never repaired or explained.**~~ Done 2026-09-15, same entry (BaGet #298,
  #552; BaGetter #211).

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


### Protocol and connector gaps from the cross-check

All done 2026-09-15 (docs/status.md, "The cross-check's Soon items"):

- ~~**Symbol pushes ignored `AllowOverwrite` and left replaced PDBs behind**~~ (BaGet #688).
- ~~**`Packages(Id=,Version=)` compared the version as text.**~~
- ~~**`GET /nuget/{feed}/package/{id}` without a version was not served.**~~
- ~~**`version=latest` and `latest-unstable` on the management download.**~~
- ~~**An upstream credential could not carry a user name.**~~ `user:password` in the secret now sends that user.
- ~~**API access tokens, from the pre-publication review:**~~ the 24-hour cap over the token's whole life, one check of a
  refused token per request, the keys of a removed issuer dropped.

### ~~When the repository goes public: split validation from publishing~~ (built 2026-09-16)

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

That shape is now in `ci.yml`: `build-and-test` and the container smoke test still run on every push to
main - the smoke test has already caught a real defect, a project added without its `COPY` line in the
Dockerfile's restore layer - and a separate `publish` job runs only for a `v*` tag, needs both of them, and
pushes to GHCR. It also refuses to run unless the repository is public, so a tag made too early publishes
nothing.

### ~~Pin the SDK and runtime base image tags~~ (done 2026-09-15: `sdk:10.0.401`, `aspnet:10.0.12`)

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
- ~~**S9.2** A sweep or `figet verify` that lists files no row names.~~ Done 2026-09-15 as **Admin → Storage check**
  (docs/status.md, "The last of the backlog").
- ~~**Name uniqueness across feeds and alternate names** is a check in the store, not an index: a `Names` table with the
  unique index, written by both paths.~~ Done 2026-09-14 (docs/status.md, "Names as one table").
- ~~**Hash verification on v2 cache fill.**~~ Done 2026-09-15, same entry.
- ~~**S1.3** Locked-out answers say a name exists after five attempts.~~ Done 2026-09-15, same entry: a name nobody has locks
  out after the same attempts. **S7.2** rate limits are per replica;
  the two connector display divergences (review 3.4). ~~S5.1 reserved IPv4 ranges; the appearance page's "Reload packs"
  acts on one replica.~~ Done 2026-09-14, same entry.
- **A download attempt for an id no upstream lists** is still made per upstream. Kept on purpose: a package published a
  minute ago can be downloadable before a gallery's listing shows it, and the attempts are bounded by the rate limit.

## Later

- **History tab** on a version: the audit log exists now (filter by feed on its page); a per-version view of it is not built.
- **Usage per version.** Usage per feed is counted since 2026-09-14 (`FeedUsage`, the graph under the feed lists). What is
  not: which versions are used, and whether a download came from the cache or the upstream. That needs per-version
  records, heaviest by volume, and belongs apart from both the per-feed counts and the audit log.
- **Prepare the repository for publication**: the history rewrite, wording that assumes a private repository, contributor
  and security files, and a README section that runs the image. The owner keeps the detailed checklist.
- **Smaller items from the issue cross-check (2026-09-15):** ~~counting management-API downloads~~ and ~~skipping an
  upstream dependency without an id~~ done 2026-09-15 (docs/status.md, "The cross-check's Later items"). Extra API-key
  header names as a setting: not built, since the scripts in use send `X-ApiKey`, which works; revisit if one sends
  another name.
- ~~**Test evidence the cross-check found missing**~~: all fourteen tested 2026-09-15, same entry; none found a defect.
- ~~**Open questions a fixture would settle**~~: all five settled 2026-09-15 (docs/status.md, "The last of the backlog"). One
  unparsable version on a v2 upstream lost the whole id - fixed; a redirected or chunked upstream download, a client
  dropping a download, Chocolatey 2.7.4 and a UNC storage root all work as they should.
- ~~**Plan and code disagree**~~: resolved 2026-09-15. Search reaching upstreams always is now what the build plan says; the
  total-size cache cap is dropped (see *Decided against*). An upstream that fails to answer is now not asked about unknown
  ids for 30 seconds, and a local test checks both providers' migrations against the model.
- ~~**Cross-check three more trackers**~~: done 2026-09-15 (PSResourceGet 936 issues, 246 read; Gitea's 50 NuGet issues;
  NuGet/Home 1,588 screened, 110 read). All thirteen findings fixed the same day (docs/status.md, "Three more trackers").
  What they left is done too (2026-09-16): a large asset upload or archive import that authenticates by challenge was
  indeed reset, and now has its refused body read like a push; the PSResourceGet client behaviours are a public page
  ("PowerShell clients"); and the behaviours with no test have one each, in `PackageEvidenceTests.Clients.cs`.

## Decided against

- **Scanning uploads for malware** (designed 2026-09-14, dropped by the owner 2026-09-16). A ClamAV daemon beside FiGet,
  checked on every write path. Known malware only, false positives on installers and scripts, and a second service to
  run and keep fed with signatures; the design is kept outside this repository.

- **A total-size cap on cached packages** (planned in build plan section 5, dropped by the owner 2026-09-15). Pruning by
  age and by last use already bounds the cache, and a volume's size is watched where the volume is.

- **A picture on the account** (Gravatar, suggested and dropped 2026-09-14). The owner sees no place it would be used, and
  a Gravatar is an image the browser fetches from gravatar.com by a hash of the account's email: every page with the menu
  bar would tell a third party which hashed addresses use this server, and show a broken image without internet.

- **Promotion between feeds** (dropped by the owner, 2026-09-13). The server being replaced has it; nothing in the
  fleet uses it, and pushing the same package to the other feed does the job.

- **Per-feed version filtering on proxy feeds.** A module whose newest release drops support for an older
  client is the module's compatibility problem; FiGet offers every upstream version and the client picks.
  Recorded in build plan section 9 so it is not re-litigated.
