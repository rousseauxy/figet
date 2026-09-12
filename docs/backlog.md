# Backlog

What is not built yet, and why it is not built yet. `docs/status.md` is the opposite of this file: it
records what *was* done, dated, with the evidence. Nothing is listed here without a reason it is not
simply done now — either it depends on something that does not exist, or it is a decision nobody has
taken.

Ordered roughly by when it is likely to be worth doing, not by importance.

## Next

### The admin area, with its own side menu

The management controls sit among the public pages. Feeds, tokens, upstreams and appearance belong
behind one navigation, leaving the public pages read-only. The stylesheet for it is already ported
(`fg-admin-shell`, `fg-admin-nav` and friends), so this is markup and routing rather than design.

### A role above admin, and what a token may create

Raised 2026-09-12. Today there is one signed-in role, and `TokenScopes` (Read, Push, Delete, Admin) says
what a *token* may do rather than what a *person* may do. The proposal: a super admin above admin, both
able to create tokens, with only the super admin able to revoke or delete them.

Worth settling before it is built, because the obvious implementation does not actually restrict
anything: **an admin who can create tokens can create an admin token**, use it, and delete whatever they
like. "Admins cannot delete tokens" only means something if token creation is itself capped — nobody may
mint a token carrying more than they hold. That rule is the feature; the menu item is the easy part.

This also wants settling against the identity model rather than the token model. Build plan section 8
already has Reader / Publisher / FeedAdmin / Admin coming from an OIDC group claim, so a fifth tier
should be defined there and not bolted onto the scope flags, or the two will disagree the moment sign-in
stops being "paste an admin token".

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
