# FiGet — working agreement for agents and humans

Read `docs/build-plan.md` before touching code. It is the specification; this file is the
house rules.

## What this is

A self-hosted NuGet v2 + v3 package server with proxy feeds, asset directories and OIDC, in
ASP.NET Core. MIT, public once phase 2 of the build plan is green. Until then the GitHub repo
is private and the forge mirror is read-only.

## Rules

- **English everywhere**: identifiers, comments, commit messages, docs.
- **Nothing organisation-specific in this repo.** No customer hostnames, feed names, API keys,
  recorded traffic with real package names, or paths from a work machine. Fixtures are
  synthetic or scrubbed. The repo will be public; treat every commit as already public.
- **No vendor or product names of the server FiGet replaces**, nor of its company or its tools. Say
  "the reference server", "the reference client" or "the commercial server being replaced". URL
  compatibility is described by the URLs themselves, never by naming whose they are.
- **Commit messages** in the imperative, one change per commit, no trailers, no
  `Co-Authored-By`.
- **Stage explicit paths.** Never `git add -A` or `git add .`.
- **Do not push container images while the repo is private.**
- **Both database providers stay green**: SQL Server (primary, production) and SQLite
  (secondary, stand-alone). No provider-specific SQL in the model or queries; two migration
  assemblies. A change that passes on one and not the other is not done.
- **Protocol behaviour is test-driven from fixtures.** A v2 or v3 endpoint is changed by first
  adding or changing the golden fixture, then the code. Unparsed OData is a 400 with the
  expression logged, never an empty 200.
- **Merged version list rule** (see build plan): for any package id, every listing endpoint
  returns the union of local and upstream versions, de-duplicated, with exactly one entry
  flagged latest, computed after the merge. Do not "optimise" this away.
- **Container rules**: non-root, arbitrary UID, port 8080, writes only under the configured
  data directories and temp. Anything else breaks OpenShift.
- **No secrets in config files.** Environment variables or mounted files, documented in
  `docs/configuration.md` as it grows.

## Toolchain

- .NET 10 SDK (see `global.json`), C# with nullable and implicit usings on. Warnings are errors.
- Tests are xUnit v3 on Microsoft.Testing.Platform (`global.json` opts `dotnet test` into it). Run a
  single project with `dotnet test --project <csproj>`.
- `dotnet build`, `dotnet test` from the repo root must pass before any push. Run the SQL Server half
  locally when persistence changes:
  `FIGET_TEST_SQLSERVER="Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True" dotnet test`.
- A migration goes into both `FiGet.Infrastructure.Sqlite` and `FiGet.Infrastructure.SqlServer`
  (`dotnet tool restore`, then `dotnet ef migrations add <Name> --project <provider project> --output-dir Migrations`).
- Stop any running FiGet instance before building: a running `FiGet.Web.exe` locks its output and the
  build keeps the old binary.
- Record what was demonstrated in `docs/status.md`: the command, the client and version, the result.
- Compatibility scripts under `tests/FiGet.Compat/` run on a Windows machine with Windows PowerShell
  5.1 and PowerShell 7 installed. They are not part of `dotnet test`; run them before claiming
  a client works.

## Layout

```
src/        layered, see build plan §3
tests/      unit and integration tests (dotnet test), FiGet.Compat/ for real-client scripts
docs/       build plan, protocol notes, configuration reference, decisions/ for ADRs
deploy/     Dockerfile, compose example, Helm chart (later phases)
```

## Layers

Dependencies point one way only, and `LayerBoundaryTests` fails the build when one leaks:

| Project | Holds | May reference |
|---|---|---|
| `FiGet.Domain` | Entities and the rules that are true regardless of how anything is stored or served: the merged version list, version ordering, search-query parsing, feed naming | `NuGet.Versioning`, nothing else |
| `FiGet.Application` | What the server does — caching from an upstream, ingesting a push, validating a token — against the ports in `Ports/` | Domain, logging **abstractions** |
| `FiGet.Infrastructure` | The adapters: EF context and stores, filesystem storage, the upstream client over `NuGet.Protocol`, the `NuGet.Packaging` indexer | Domain, Application, anything it needs |
| `FiGet.Infrastructure.Sqlite` / `.SqlServer` | Nothing but migrations for that provider | Infrastructure |
| `FiGet.Http`, `FiGet.Protocol.V2`, `FiGet.Protocol.V3` | ASP.NET helpers and the two protocol surfaces | Domain, Application — never EF |
| `FiGet.Web` | The composition root: the only project that binds ports to adapters | everything |

A new dependency on the outside world is an interface in `FiGet.Application/Ports/` and an
implementation in `FiGet.Infrastructure/`, wired up in `FiGetApp.ConfigureServices`. If a boundary test
fails, the question is not how to make it pass but what leaked in and which layer it belonged to.
