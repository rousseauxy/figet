# Backlog

What is not built yet, and why it is not built yet. `docs/status.md` is the opposite of this file: it
records what *was* done, dated, with the evidence. Nothing is listed here without a reason it is not
simply done now — either it depends on something that does not exist, or it is a decision nobody has
taken.

Ordered roughly by when it is likely to be worth doing, not by importance.

## Next

### Watch the PSResourceGet fix (comment posted 2026-09-13)

Not a change to this server. PSResourceGet chooses a download URL by substring match on the version, so a
requested version that is a text prefix of a longer one installs the wrong package (`docs/status.md`, under
NuGet v3). It is open as PowerShell/PSResourceGet #1657, with an unmerged
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

## Soon

- **Record the asset write side from the reference server.** Its uploads, deletes and metadata were taken
  from the client library and the documentation, because writing to the reference instance was not possible
  in the session that built them. Still unconfirmed: the status of a `PUT` onto an existing file (FiGet
  answers 409), the header name for user metadata marked `includeInResponseHeader` (FiGet sends none), and the
  body of an import response (FiGet answers counts). The reference client has since run every asset command
  against FiGet, which settled the metadata shape. Needs an API key for the reference instance.

### Phase 6: what the review left for the cluster deployment

From `docs/reviews/2026-09-14-architecture-security.md`, with the answers the cluster's operators gave the same day:
secrets come from an external secret store, the chart is Helm, storage may be S3 or a ReadWriteMany volume.

- **The Helm chart** must set `FiGet:PublicBaseUrl`, `Database:ExpectedReplicas`, `FiGet__DataProtection__MasterKey`
  from a synced Secret (FiGet refuses to start with more than one replica without it), upstream credentials as
  `FIGET_UPSTREAM_*` variables from the same kind of Secret, `Storage:TempPath` on the volume or an `emptyDir`, and a
  proxy body limit of at least `MaxAssetSizeMB`.
- **Trust only the ingress for forwarded headers** (S10.3): `ASPNETCORE_FORWARDEDHEADERS_ENABLED` trusts every peer, so
  either configure `KnownNetworks` to the ingress range or keep the pod reachable only through it with a NetworkPolicy.
- **Storage: start on a ReadWriteMany volume.** It needs nothing new: the file-system storage already writes
  atomically and is keyed by feed. S3 would need the storage ports to take a byte range (a download is served with
  Range from a seekable stream today) before an adapter; not worth doing unless the platform prefers S3.
- **Secrets stay environment variables.** The external secret store syncs into a Secret the pod reads as variables, which is what
  `CredentialRef` and the connection string already use; a separate secret-source port (mounted files) is not needed.

### Smaller items from the 2026-09-14 review

Each is Low, and none is reachable without an account that already has rights; ids refer to the review.

- **A download attempt for an id no upstream lists** is still made per upstream. Kept on purpose: a package published a
  minute ago can be downloadable before a gallery's listing shows it, and the attempts are bounded by the rate limit.

## Later

- **History tab** on a version: the audit log exists now (filter by feed on its page); a per-version view of it is not built.
- **Usage per version.** Usage per feed is counted since 2026-09-14 (`FeedUsage`, the graph under the feed lists). What is
  not: which versions are used, and whether a download came from the cache or the upstream. That needs per-version
  records, heaviest by volume, and belongs apart from both the per-feed counts and the audit log.
- **Prepare the repository for publication**: the history rewrite, wording that assumes a private repository, contributor
  and security files, and a README section that runs the image. The owner keeps the detailed checklist.
- **Extra API-key header names as a setting.** Not built: the scripts in use send `X-ApiKey`, which works. Revisit if a
  client sends another name.

### Left out of the change report (built 2026-09-16)

- **`GetUpdates()` batching for the catalogue sweep.** A v2 gallery answers "anything newer for these ids" in one
  request, and FiGet already serves that shape itself. The sweep refreshes one id at a time instead, which is bounded
  but not cheap for a feed of hundreds. Worth doing when a sweep starts taking longer than the day it runs on.
- **A Matrix sender of FiGet's own.** Matrix has no incoming webhook: it needs a room id and an access token against
  its client API. That is a second kind of credential and a URL shape that is not a webhook, so a relay - which every
  Matrix deployment already has for its other alerts - does the job today.
- **E-mail.** No SMTP anywhere in this server, and adding it means a mail library, credentials, retries and bounce
  handling. A webhook plus whatever already sends mail is smaller.
- **Release notes for a v3 upstream.** A registration leaf carries none, and the catalog resource that would is a
  non-goal (build plan section 2). Those rows show a link instead.
- **A scheduled storage check.** The button exists; nobody asked for it to run by itself. It would be a lease name and
  an interval, in the shape the other jobs now have.
- **Per-feed body formats.** One format per server today. A second per-feed setting beside the address if two feeds
  ever need different shapes.

## Decided against

- **Scanning uploads for malware** (designed 2026-09-14, dropped by the owner 2026-09-16). A ClamAV daemon beside FiGet,
  checked on every write path. Known malware only, false positives on installers and scripts, and a second service to
  run and keep fed with signatures; the design is kept outside this repository.

- **A total-size cap on cached packages** (planned in build plan section 5, dropped by the owner 2026-09-15). Pruning by
  age and by last use already bounds the cache, and a volume's size is watched where the volume is.

- **A picture on the account** (Gravatar, suggested and dropped 2026-09-14). The owner sees no place it would be used, and
  a Gravatar is an image the browser fetches from gravatar.com by a hash of the account's email: every page with the menu
  bar would tell a third party which hashed addresses use this server, and show a broken image without internet.

- **Promotion between feeds** (dropped by the owner, 2026-09-13). The server being replaced has it; none of the
  consumers that will move to FiGet use it, and pushing the same package to the other feed does the job.

- **Per-feed version filtering on proxy feeds.** A module whose newest release drops support for an older
  client is the module's compatibility problem; FiGet offers every upstream version and the client picks.
  Recorded in build plan section 9 so it is not re-litigated.
