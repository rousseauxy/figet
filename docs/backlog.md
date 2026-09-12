# Backlog

What is not built yet, and why it is not built yet. `docs/status.md` is the opposite of this file: it
records what *was* done, dated, with the evidence. Nothing is listed here without a reason it is not
simply done now — either it depends on something that does not exist, or it is a decision nobody has
taken.

Ordered roughly by when it is likely to be worth doing, not by importance.

## Next

### Find-Module is slow for a package with thousands of versions

`Find-Module PnP.PowerShell` took 44s over v2 where `Find-PSResource` took 3.1s over v3 (2026-09-12).
The v2 path builds a merged row for all 2098 versions, then filters, orders and pages them, and the Atom
writer serialises what survives. The v3 path pages first.

Not the catalogue fetch - that is cached and shared by both. Measure before changing anything: the
suspicion is the per-row work in the filter and the writer, not the merge.

### Stop the proxy compressing package downloads

`Save-Module` fails for every package through the public hostname, with a zip error, because the proxy
gzips `application/zip` and drops `Content-Length` doing it (docs/status.md, "Save-Module fails through the
proxy"). The NuGet provider under PowerShellGet 2.2.5 - the fleet's pinned stack - cannot read that; the
same client saves the same package from the PowerShell Gallery without trouble, and `Save-PSResource` over
v3 succeeds against the identical compressed bytes.

Nothing in this repository can fix it: the application already sends a correct length and never compresses.
The change is one middleware exclusion in the proxy that fronts every service, so it is not ours to make
unilaterally - but until it is made, the v2 install path is broken for exactly the clients this server
exists to replace the commercial server for.

Worth a compatibility test afterwards that drives the real 5.1 client against a running instance, since
nothing in CI would have caught this: every protocol test here talks to the application directly, and the
defect only exists between the proxy and an old client.

### Let the descriptions survive a restart

Stale-while-revalidate is done (docs/status.md, "The catalogue outlives the request now"): the version list
lives in the database, anything cached is served at once whatever its age, and a stale catalogue refreshes
behind the request. A cold `PnP.PowerShell` page went from 15.02s to 0.40s, and the recurring 13-15s every
five minutes is gone.

What is still in memory is the *descriptions*, and that is deliberate after one attempt at the obvious:
persisting them took the page down with an `OutOfMemoryException`, because describing PnP's 2098 versions
is 101 MB against 31 KB of version strings. So after a restart the first view of each package lists plainly
until the refresh lands - correct and instant, just undetailed for a few seconds.

Making that survive properly needs the volume reduced first, not the storage changed, which is why the item
below is now the interesting one: a listing renders ten rows and a page of the full list renders fifty, and
those are the only descriptions anyone sees. Storing what is rendered is kilobytes. Storing everything is
not.

### Ask upstreams only for what the caller will serve

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

### The admin area, with its own side menu

Includes redoing the feed settings page, which is the worst layout in the application (noted
2026-09-12 from using it). Specifically, and these are layout faults rather than styling ones:

- **The panel grid leaves a large dead area.** Three panels of wildly unequal height sit in one row, so
  a short Settings panel and a short Source URLs panel park beside a very tall Upstreams panel, and the
  danger zone ends up orphaned far below with nothing beside it.
- **Source URLs wrap mid-token**, breaking a copyable URL across three lines in the middle of a word.
  They want their own full-width row, not a narrow column.
- **The upstreams table scrolls sideways inside its column** rather than being given the width a table
  needs.
- **Adding an upstream is a seven-field form** taking up most of the page height, permanently, for
  something done rarely. It belongs behind a control rather than always open.

The shape to aim for is the same side-menu admin the sibling application uses, with each of these as its
own section rather than four unrelated things competing for one three-column grid.

The management controls sit among the public pages. Feeds, tokens, upstreams and appearance belong
behind one navigation, leaving the public pages read-only. The stylesheet for it is already ported
(`fg-admin-shell`, `fg-admin-nav` and friends), so this is markup and routing rather than design.

### Unlist a cached copy the upstream has unlisted, not only one it has removed

`ReconcileWithdrawnAsync` compares what an upstream still *offers* against the copies cached here and
unlists the ones that have disappeared. It never asks whether the upstream still *lists* what it offers,
so a cached copy of a version the gallery has unlisted stays listed here.

Visible on the live instance (docs/status.md, "The 37th version"): the gallery advertises 36 versions of
PnP.PowerShell and the page shows 37. The extra one is `1.9.61-nightly` — cached here, unlisted there.

Both states mean "stop offering this", so the rule is half applied, and the connector now knows the flag,
so the change itself is small. What makes it a decision rather than a fix is the argument on the other
side: what this feed *holds* is arguably this feed's business, and an air-gapped fleet may deliberately
keep a version the gallery has since hidden. Unlisting it here would hide it from that fleet's own
listings, though it would stay installable by exact version.

Worth settling with the admin area, where a held-but-unlisted version finally has somewhere to be seen.

### Somewhere to manage a held version that is unlisted

The package page now mirrors the gallery and shows no unlisted version at all (docs/status.md, "What the
tables show"). That is right for a reader, and it leaves one thing with nowhere to live: a version this
feed *holds* and has unlisted — either because the feed's deletion behaviour is `Unlist`, so a delete
through the API unlists rather than removes, or because an upstream withdrew it and the connector unlisted
the cached copy.

Nothing is lost and nothing is unrecoverable: the version still downloads by exact version, and
`POST /v3/publish/{id}/{version}` relists it. But no screen admits it exists, so the only way to find one
is to already know its version number.

Belongs in the admin area as a view over what the feed *holds* rather than what it advertises — unlisted
copies listed, with relist and delete beside them. That is the one place where "show me everything" is the
right default, and having it is what lets the reader-facing page stay honest about mirroring the gallery.

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

### Dependencies and metadata for a version nobody has cached

Asked for 2026-09-12: the server being replaced shows a package's dependencies and versions without
caching anything first. FiGet already lists upstream versions and describes them (description, authors,
tags), but the Dependencies tab is empty until the package is here. NuGet's metadata resource returns
`DependencySets` in the same call already being made for the description, so this is plumbing
`UpstreamMetadata` through to the placeholder rows rather than new network traffic.

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

### Versions the gallery hides look listed for a moment after every restart

Measured 2026-09-12. On start the description cache is empty - it lives in memory, which is deliberate
after the 101 MB incident - so the first catalogue read has nothing described. `ReconcileWithdrawnAsync`
then takes the safe branch, where `advertised` is null and only presence counts, and a cached copy the
gallery unlists is present in the version list. So it gets listed again, until the first described refresh
puts it back.

It is wider than cached copies. Upstream versions carry their listed flag from the same descriptions, so
while those are cold the whole listing shows versions the gallery hides. Observed deliberately on a
restart rather than inferred: at 20:48:20, seconds after start, `PnP.PowerShell` read "1 to 50 of 2098
versions" with no hidden line at all; at 20:48:47 the same page read "1 to 36 of 36 versions, 2062 unlisted
hidden". Same data, same image, 27 seconds apart.

Two earlier descriptions of this were wrong and are corrected here. It is not a loop - it is one cycle per
container start, one re-list and one correction. And the window is **short**, not minutes: it opens when a
package is first viewed after a restart and closes when that view's background refresh lands, which for the
2098-version package took under 30 seconds.

It still matters because inside that window an unlisted version can win "latest" - the one thing the
reconcile exists to prevent, and exactly how `PowerShellGet 2.2.5.1` kept being served before it was
written.

The cheap fix is to make re-listing require positive evidence: unlist on presence alone, but only list
again when the upstream actually described the version as listed. Null `advertised` would then mean "no
news", not "everything is fine". The thorough fix is to let the descriptions survive a restart, which is
its own entry above, and would close this as a side effect.

### An audit log: who changed what, and when

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

## Soon

- **Promotion between feeds.** Referred to by the server being replaced; nothing in FiGet does it yet.
  Needs a decision on whether a promoted package keeps its origin or becomes a push.
- **The packages management API** (`/api/packages/{feed}/{versions|latest|delete}`), build plan section
  4.5. The retention and version-fallback scripts in the fleet call it, so the cutover needs it.
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

- **Asset directories** (phase 4): `/endpoints/{dir}/content/{path}`, upload through the UI with
  drag-and-drop, and a size limit that accommodates a .NET hosting bundle.
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
