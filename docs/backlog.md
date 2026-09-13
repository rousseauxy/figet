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
2. **Enable response compression** for `/nuget` regardless. Does not fix the time measured on a fast link,
   but 80 MB to 10 MB matters to servers on a slow line. Low risk.
3. **Trim tags on older versions only** (keep them on the latest few). Cuts the payload most, but
   `Find-Module -AllVersions` would show no `Includes` for old versions. User-visible.
4. **Accept it.** The client asks for every version by design; PSResourceGet over v3 is already fast.

### Let the descriptions survive a restart

Half of this is done, deliberately. What changes an *answer* now survives a restart: which versions an
upstream hides, and what each version depends on, are stored beside the version list (docs/status.md, "The
facts that matter now survive a restart"). A cold start no longer re-lists a hidden version, and no longer
tells a client that a module depends on nothing.

What still does not survive is the text: descriptions, summaries, authors and tags. After a restart a
listing reads plainly until the first refresh describes it - display only, never correctness. They stay in
memory on purpose: the tags alone are what made the persisted form a hundred megabytes and an out-of-memory
crash. If this is ever worth doing it wants a different shape - per-version rows loaded on demand, or the
tags left out - not the single blob that failed.

### Ask upstreams only for what the caller will serve

**Read this first - its premise changed on 2026-09-13.** Descriptions are no longer cosmetic. The `listed`
flag and each version's dependencies now come from the same described call, and both change what a client
is told: skipping them brings back a first install that pulls in no dependencies, and hidden versions that
look current. Those two facts are also persisted now, but a versions-only fetch would stop *refreshing*
them. So "versions only" is not free for any caller that feeds a registration, a v2 entry or the
withdrawal reconcile - which is most of them. Only the flat-container version list truly needs nothing but
versions.

It also pays off only against a **v3** upstream, and the one upstream in production is the v2 PowerShell
Gallery, where this saves nothing. Not worth doing until a v3 upstream exists, and then only for callers that
can prove they read neither flag.

---


Raised 2026-09-12, from the observation that a client calling the API usually needs only versions. True,
and the shapes agree: `/v3/flatcontainer/{id}/index.json` returns a bare version array, and a registration
index above 128 versions inlines no leaves, so neither needs a description at all.

Worth doing only against a **v3** upstream, where versions cost 0.17s and descriptions 3.2s. Against a v2
gallery it saves nothing, and asking for versions alone is actually *dearer* than asking for both (13.4s
versus 8.4s), because both come from the same paged walk. Since the feed that hurts is v2-backed this is a
real but secondary win. Shape: a describe-what-you-render call on the port, with the four callers asking
for their own rows.

One thing it cannot drop: a v2 Atom entry carries `Tags`, and PowerShellGet reads `PSEdition_Desktop` /
`PSEdition_Core` from them to decide whether a version can run at all. Descriptions are cosmetic there;
tags are not.

### A role above admin, and what a token may create

Raised 2026-09-12. Today there is one signed-in role, and `TokenScopes` (Read, Push, Delete, Admin) says
what a *token* may do rather than what a *person* may do. The proposal: a super admin above admin, both
able to create tokens, with only the super admin able to revoke or delete them.

**Settled 2026-09-12: only a super admin may issue admin or super-admin tokens.** An admin may still
create read, push and delete tokens; it is the top two levels that are reserved. This is the rule that
makes the rest work, because the obvious version restricts nothing — an admin who can create *any* token
can create an **admin** token, sign in with it, and delete whatever they like. Generalised: nobody may
mint a token carrying more than they hold. That ceiling is the feature; the menu item is the easy part.

**This has to survive SSO, and SSO does not solve it.** Build plan section 8 has Reader / Publisher /
FeedAdmin / Admin arriving from an OIDC group claim, so the fifth tier belongs there and not bolted onto
the `TokenScopes` flags, or the two disagree the moment sign-in stops being "paste an admin token". Note
what moves and what does not: with SSO, *who is an admin* becomes the identity provider's answer, so
granting the admin role leaves FiGet entirely and becomes group management in the IdP. What does **not**
move is the ceiling on minting — a signed-in admin still issues API tokens from inside FiGet, and
nothing in the group claim stops them issuing one above their own level. The check belongs in the token
service regardless of where the role came from.

### Sortable columns on the package grid

The grid added 2026-09-12 filters and pages, but does not sort. QuickGrid sorts for free only when it is
given an `IQueryable`; this one is fed by `IPackageStore.SearchAsync`, a port with skip, take and a
filter but no ordering. Pointing the grid at EF directly would put queries in the composition root and
undo the layering, so the honest fix is a sort parameter on the port plus both EF implementations.

### Pull should cache the dependency closure, not one package

Raised 2026-09-12. **Pull is currently the only path that does not leave a working offline copy.**
`EnsureCachedAsync` fetches exactly one `(id, version)`, so pulling `Microsoft.Graph` from the UI caches
one nupkg — and the air-gapped machine the pull was *for* then fails on first install.

A client install already gets this right, and not because FiGet is clever: the client resolves the graph
itself and asks for each id separately, which trips look-through per package. The phase 0 recording
proves it — `tests/fixtures/powershellget-2.2.5/meta-save-module-old-version.json` contains **39
downloads for one `Save-Module`**: `Microsoft.Graph` 2.30.0 and 38 sub-modules, every one pinned to
2.30.0. So the closure gets cached when a client does it, and does not when the UI does it.

Four things to decide before building it, none of them obvious:

- **Dependencies are only known after the package is here**, because they are read from its nuspec. So
  this is a walk — pull, read, resolve, pull again — not a lookup, and it wants a visible result saying
  what it fetched.
- **Resolving a range to a version** is a real rule, not a guess: NuGet takes the lowest version that
  satisfies the range. Getting this wrong quietly caches something nobody will ask for.
- **A cap.** Microsoft.Graph is 39 packages and hundreds of megabytes; something with a looser range
  could be far worse. Depth and count limits, and a refusal that explains itself.
- **Which framework group.** A .NET package has dependency groups per target framework and following
  all of them explodes; a PowerShell module has one flat set, which is the case that matters first.

### An audit log: who changed what, and when

**The console half shipped on 2026-09-13** (docs/status.md, "The flapping is fixed at the cause, and there is an audit log"): every change below is written as a line under the `FiGet.Audit` category. What remains is the database table and the admin page.

Decided 2026-09-12, to be built after the current round of testing settles. Distinct from the request log
that now exists: that one answers "did a client reach us and what did it ask for", at Information level on
the console, opt-in via `FiGet:Logging:Requests`. This answers "who changed this", which is a different
shape, a different audience and a different retention question - so it is not a wider middleware.

**What it records.** Three kinds of event:

- **Admin changes** - feed created, deleted or edited, upstream added or removed, token issued or revoked,
  theme changed, a package un-cached. Who, when, and what changed.
- **Package lifecycle** - push, delete, unlist, relist, admin Pull, with the token or user behind it. This
  overlaps the request log on purpose: the request log has the HTTP call, this has the intent.
- **Authentication events** - sign-ins, and rejected or revoked token attempts. A client still presenting a
  dead key is invisible today; `claude-push` was revoked on 2026-09-12 and nothing would show an attempt
  to keep using it.

**Where it lives.** A database table with an admin page beside Feeds and Tokens, filterable by feed, user
and date. Not the console: the container keeps a single 50 MB `json-file` with no rotation history, so
console-only records are lost silently, and "what happened last Tuesday" is exactly what an audit log is
asked. `FeedAccess.ResolveAsync` already resolves feed and token for every protocol request and is where
attribution should come from, the same as the request log's `who=`.

**Retention: configurable days, pruned by a background task**, designed in rather than added later. The
server being replaced keeps these forever and its own documentation warns the table reaches gigabytes,
recommends purging annually, and supplies manual `DELETE` scripts because pruning "is not built-in".

**Not in this entry:** per-download records carrying whether a version was served from cache or fetched
upstream. It is the one thing the commercial server cannot answer - its guidance is to infer it from
`time-taken` - and it is cheap here because `ConnectorService` already knows. Left out because it is the
heaviest by volume and belongs with usage statistics and cache pruning, not with an audit trail. Worth
doing; not yet decided when.

### Add our reproduction to the PSResourceGet fix

Not a change to this server. PSResourceGet chooses a download URL by substring match on the version, so a
requested version that is a text prefix of a longer one installs the wrong package (docs/status.md, "An
install that fetched the wrong version"). It is already open as PowerShell/PSResourceGet #1657, with an
unmerged fix in PR #2019.

That fix rests on a single private-feed report. A comment adding a reproduction against public gallery
packages - PowerShellGet 2.2.4 against 2.2.4.1, both listed, on PSResourceGet 1.2.0 - would give it a
public case and a current version. It is outward-facing, so it waits for a person to post it.

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
- **Replay the recorded fixtures as tests** (section 7.1). The fixtures are committed and the shapes they
  carry are covered by hand-written tests, but nothing reads the fixture files.
- **Run the real Windows PowerShell 5.1 client** against the v2 surface (section 7.2). The compat scripts
  exist and need pointing at a running instance.
- **Per-version registration leaves for upstream-only versions.** A v3 client can list them but cannot
  read a leaf for one that has never been downloaded.

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
- **History tab** on a version, which needs the audit log of phase 5.
- **Usage and statistics**, which needs per-version download tracking — the same data cache pruning wants.
- **Cache pruning and retention** by age and use.
- **OIDC sign-in** (phase 5), provider-agnostic, which is where the role model above belongs.

## Decided against

- **Per-feed version filtering on proxy feeds.** A module whose newest release drops support for an older
  client is the module's compatibility problem; FiGet offers every upstream version and the client picks.
  Recorded in build plan section 9 so it is not re-litigated.
