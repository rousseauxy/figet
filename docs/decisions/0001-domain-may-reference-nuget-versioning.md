# 0001 — The domain may reference NuGet.Versioning, and nothing else

## Context

`FiGet.Domain` is the bottom layer: entities, and the rules that hold no matter how anything is stored
or served. The reference implementation this layout is modelled on keeps its domain project at *zero*
package references, and says so in a comment, because a domain that can see `HttpContext` or an EF
attribute stops being a domain.

FiGet cannot copy that literally. Its central rule — the merged version list of build plan §5 — is
entirely about comparing versions: which of two versions is newer, which single version is latest,
whether a version is a prerelease, whether it is SemVer 2. Those are not arithmetic. NuGet's version
ordering has specific, documented behaviour around build metadata, four-part versions, leading zeroes
and prerelease tags, and every NuGet client in the world resolves against it.

Writing our own comparer would mean this server disagreeing with nuget.org about which version is
newest. That disagreement would not show up as an error. It would show up as `Update-Module` installing
the wrong version on a subset of packages, months later, on someone else's machine.

## Decision

`FiGet.Domain` carries exactly one package reference, `NuGet.Versioning`, and no others.

`LayerBoundaryTests.Domain_references_NuGetVersioning_and_no_other_package` asserts the list is exactly
that, so the exception cannot quietly widen. The neighbouring NuGet packages are explicitly forbidden by
name in the same test file: `NuGet.Protocol` speaks HTTP to other servers and `NuGet.Packaging` opens
files on disk, so both are adapters regardless of who publishes them.

## Consequences

- Version ordering matches every other NuGet server, because it is the same code.
- The rule "the domain has no package references" is now "the domain has one, and the test names it",
  which is a weaker sentence and needs this record to stay meaningful.
- The plausible-looking wrong move is to reason from the exception: *`NuGet.Versioning` is allowed, so
  `NuGet.Packaging` must be fine too — it is the same vendor, and reading a nuspec feels just as
  fundamental as comparing a version.* It is not. Reading a `.nupkg` means opening a zip off a disk or a
  stream, which is the definition of an adapter. That is why `IPackageIndexer` is a port in
  `FiGet.Application/Ports/` with its implementation in `FiGet.Infrastructure/Packages/`, and why the
  test forbids the package by name rather than trusting the reasoning.
- To revisit: the only thing that would justify dropping `NuGet.Versioning` is NuGet itself changing its
  ordering rules, at which point matching the ecosystem stops being the argument for using their code.
