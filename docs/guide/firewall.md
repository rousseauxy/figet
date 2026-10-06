# What to open in a firewall

Everything below was measured against the released image rather than read off a list, because the host that answers a
listing is often not the host that serves the download — and allowing only the first gives you a server that finds
packages and cannot fetch them.

A standard install needs **one way in and one way out**: clients reach FiGet over HTTPS, and FiGet reaches the galleries
you proxy. Everything else on this page applies only if you switched that feature on.

## In

| From | To | Port | Why |
|---|---|---|---|
| Clients, CI, developer machines | the reverse proxy or router in front of FiGet | 443/tcp | Everything a client does: restore, push, the pages, the API. |
| That proxy or router | the FiGet container | 8080/tcp | The only port the container listens on. It speaks plain HTTP and expects TLS to end at the proxy. |

Nothing else listens. There is no separate admin port, no management agent and no inbound connection from any gallery.

If the pods must be reachable **only** through the router, say so with a NetworkPolicy: FiGet reads the client's address
from the router's `X-Forwarded-For` for rate limits, per-feed networks and the audit log, so anything else that can
connect could set that header itself.

## Out

Only what a feed is configured to use. A server with no proxy feeds needs no egress at all.

| To | Port | Why | Needed when |
|---|---|---|---|
| `www.powershellgallery.com` | 443/tcp | Listings, versions and metadata. | A feed proxies the PowerShell Gallery. |
| `cdn.powershellgallery.com` | 443/tcp | **The package bytes.** The API host redirects every download here. | The same feed — allowing only the line above gives listings that work and downloads that fail. |
| `api.nuget.org` | 443/tcp | The v3 index, metadata **and** the packages: nuget.org serves all three from one host, with no redirect. | A feed proxies nuget.org. |
| `community.chocolatey.org` | 443/tcp | Listings and metadata. | A feed proxies the Chocolatey community repository. |
| `packages.chocolatey.org` | 443/tcp | **The package bytes**, which the API host redirects to. | The same feed. |
| Your SQL Server | 1433/tcp | The database, when `FiGet:Database:Provider` is `SqlServer`. | Any deployment not on SQLite. |
| Your identity provider | 443/tcp | Discovery, keys and the token exchange during sign-in. | A sign-in provider is configured. |
| Your webhook receiver | 443/tcp | Posting the change report. | `FiGet:Changes:Webhook:Url` or a per-feed address is set. |
| Hosts in `FiGet:RemoteFetch:AllowedHosts` | 443/tcp | Fetching an asset by URL, which is refused for every host not on that list. | Someone uses "fetch by URL" on an asset directory. |
| Your collector | as configured | Traces and metrics. | `OTEL_EXPORTER_OTLP_ENDPOINT` is set. |

Plus DNS and NTP, as for any container.

**FiGet contacts nothing on its own behalf.** There is no telemetry, no update check and no licence call: every outbound
connection in the table is one a feed, a provider or a setting of yours asked for.

## Through a proxy

Set `HTTPS_PROXY`, `HTTP_PROXY` and `NO_PROXY` as environment variables and the gallery calls honour them — measured, by
pointing `HTTPS_PROXY` at a dead address and watching a listing of 462,757 bytes become 530.

Fetching an asset by URL is the exception: it has its own `FiGet:RemoteFetch:Proxy`, because that path is reached by
people rather than by the server's own jobs and is deliberately configured apart.

If the proxy inspects TLS, give the container the trust bundle — on OpenShift that is the usual
`config.openshift.io/inject-trusted-cabundle` ConfigMap mounted over `/etc/ssl/certs/ca-certificates.crt`. Without it
every gallery call fails certificate validation while everything local keeps working, which reads like a broken feed
rather than a firewall.

## What a blocked gallery looks like

Not an outage. The same measurement above left the server **healthy** — `/health/ready` answered 200 throughout — and
the proxy feed simply served what it already held: listings shrink to the cached versions, and a package nobody fetched
before cannot be downloaded.

That is worth knowing before you go looking in the wrong place. The signs are a feed whose listings are shorter than the
gallery's, a 404 for a version the gallery clearly has, and nothing alarming in the health checks. The server log names
the host it could not reach.

Two jobs reach out on a schedule and will fail quietly the same way: the daily catalogue sweep, which keeps each proxy
feed's version lists fresh, and the change report, which posts to your webhook. Both can be switched off entirely with
`FiGet:Jobs:CatalogueSweep=0` and `FiGet:Jobs:ChangeReport=0` if a deployment is meant to have no egress at all.
