# FiGet

**Files + Get.** A self-hosted package server for shops that live on PowerShell and .NET:
NuGet **v2 and v3**, so the Windows PowerShell 5.1 fleet and the newest tooling both work;
proxy feeds with caching; asset directories for installers and scripts; OpenID Connect against
any provider.

> Status: phase 1 done (NuGet v3, curated feeds, symbols, tokens, admin UI, SQL Server and SQLite). Not released.
> See [docs/status.md](docs/status.md) for what works and how it was verified, and [docs/build-plan.md](docs/build-plan.md) for the plan.

## Run it

```
dotnet run --project src/FiGet.Web
```

The first start creates a SQLite database and a feed under `src/FiGet.Web/data` and writes a one-time admin token to the
log. Sign in at http://localhost:5555 with it. Every setting is in [docs/configuration.md](docs/configuration.md);
`deploy/compose.example.yml` runs the container.

## Why

Air-gapped or egress-restricted servers still need `Install-Module` and a place to fetch
installers from. The commercial answer works but costs a licence and carries features nobody
uses. The open-source NuGet servers speak only v3 and break PowerShellGet; the git forges speak
v2 but fail `Find-Module -Name X`, and none of them proxies an upstream. FiGet exists to close
that gap and nothing else.

## What it will do

- NuGet v3: service index, registration, flat container, search, autocomplete, push, delete,
  symbols. Registration JSON shaped like nuget.org, including the `@type` markers the
  PowerShell 5.1 client requires.
- NuGet v2 OData: the subset the real clients emit, recorded from live traffic. Unparsed
  filters fail loudly.
- Proxy feeds: any number of upstreams per feed, look-through on miss, cached afterwards. One
  merged version list per package; a cached copy never shadows a newer upstream release.
- Curated feeds with retention rules.
- Asset directories: path-addressed files, plain `GET` by path.
- OIDC against any provider, several at once; API keys and personal access tokens for clients.
- One container image that runs stand-alone with SQLite or on Kubernetes/OpenShift with SQL
  Server and shared storage.

## Name

"Fi" for files, "Get" because that is what the ecosystem calls a NuGet-compatible server
(MyGet, BaGet, LiGet). Yes, it sounds like fidget.

## Licence

MIT. See [LICENSE](LICENSE).
