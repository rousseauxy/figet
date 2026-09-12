# Backlog

What is not built yet, and why it is not built yet. `docs/status.md` is the opposite of this file: it
records what *was* done, dated, with the evidence. Nothing is listed here without a reason it is not
simply done now — either it depends on something that does not exist, or it is a decision nobody has
taken.

Ordered roughly by when it is likely to be worth doing, not by importance.

## Next

### Keep the upstream catalogue long enough to matter

The two-walk defect is fixed (docs/status.md, "The 23-second page was two walks, not one describe"), which
took the upstream calls behind a cold `PnP.PowerShell` page from 21.9s to 8.4s. The remaining 8.4s is the
walk itself: 2098 versions out of a v2 gallery, which has no versions-only endpoint to be cheap about.

It cannot be made smaller from this side, so it has to be paid less often. Today it is paid every five
minutes per package: `UpstreamIndexTtl` defaults to five minutes, and the descriptions live in a singleton
that empties on every restart and is shared with no other replica.

Two changes, in this order:

1. **Serve the cached catalogue while refreshing behind the request.** A version list five minutes old is a
   fine answer; blocking a page for eight seconds to avoid it is not. Only the first ever view of a package
   should wait. The care it needs is real: fire-and-forget work in a request wants its own scope, its own
   cancellation, and de-duplication, so twenty readers do not start twenty walks of the same package.
2. **Put the descriptions in the database beside the version list**, where the version list already is.
   That survives a restart and is shared between replicas, neither of which is true today. Needs a
   migration, which is why it is second.

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
