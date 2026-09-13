# Backlog

What is not built yet, and why it is not built yet. `docs/status.md` is the opposite of this file: it
records what *was* done, dated, with the evidence. Nothing is listed here without a reason it is not
simply done now — either it depends on something that does not exist, or it is a decision nobody has
taken.

Ordered roughly by when it is likely to be worth doing, not by importance.

## Next

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

- **Fetch by URL through a proxy.** Fetching deliberately uses no proxy, because the address check would see
  the proxy rather than the target. An instance whose only way out is a proxy needs the check done another
  way - resolving and pinning the address before the request, or an allow-list of hosts - before it can fetch.
- **Record the asset write side from the reference server.** Its uploads, deletes and metadata were taken
  from the client library and the documentation, because writing to the reference instance was not possible
  in the session that built them. Still unconfirmed: the status of a `PUT` onto an existing file (FiGet
  answers 409), the header name for user metadata marked `includeInResponseHeader` (FiGet sends none), and the
  body of an import response (FiGet answers counts). The reference client has since run every asset command
  against FiGet, which settled the metadata shape. Needs an API key for the reference instance.
- **Drive the upload drop zone in a real browser.** The requests it sends - an upload, and an archive import -
  are covered by tests and the page was checked visually, but the script's drag, progress,
  confirm-before-replace and import-result path has not been clicked through by a person yet.

- **Promotion between feeds.** Referred to by the server being replaced; nothing in FiGet does it yet.
  Needs a decision on whether a promoted package keeps its origin or becomes a push.

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

## Later

- **Per-feed instruction templates** for install and file usage, so wording and the client-facing
  hostname can differ per feed.
- **History tab** on a version: the audit log exists now (filter by feed on its page); a per-version view of it is not built.
- **Usage and statistics**, which needs per-version download tracking — the same data cache pruning wants. Per-download
  records with whether a version came from cache or upstream belong here, not in the audit log: heaviest by volume.

## Decided against

- **Per-feed version filtering on proxy feeds.** A module whose newest release drops support for an older
  client is the module's compatibility problem; FiGet offers every upstream version and the client picks.
  Recorded in build plan section 9 so it is not re-litigated.
