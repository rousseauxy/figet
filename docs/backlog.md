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
