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

- .NET 10 SDK (see `global.json`), C# with nullable and implicit usings on.
- `dotnet build`, `dotnet test` from the repo root must pass before any push.
- Compatibility scripts under `tests/compat/` run on a Windows machine with Windows PowerShell
  5.1 and PowerShell 7 installed. They are not part of `dotnet test`; run them before claiming
  a client works.

## Layout

```
src/        one project per concern, see build plan §3
tests/      unit + fixture tests (dotnet test), compat/ for real-client scripts
docs/       build plan, protocol notes, configuration reference
deploy/     Dockerfile, compose example, Helm chart (later phases)
```
