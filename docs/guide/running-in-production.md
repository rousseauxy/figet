# Running FiGet in production

A single container with SQLite runs with no settings at all. Behind a reverse proxy, with more than one replica, or on a
server other people rely on, these are the settings that matter. Every key below is also an environment variable, with
`__` for each `:` (`FiGet:PublicBaseUrl` is `FiGet__PublicBaseUrl`).

## Behind a reverse proxy

- **`FiGet:PublicBaseUrl`**: the `https://` address clients use. Required behind a proxy: every URL in a protocol answer,
  the sign-in redirect URI and the copy buttons are built from it. With an `https://` address the sign-in cookies are
  also marked Secure.
- **`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`**, so the client's address and scheme come from the proxy. It does not
  take the host name from the proxy; that is what `PublicBaseUrl` is for. The switch trusts whoever connects, so only the
  proxy may be able to reach the container's port: a network policy, or a port that is not published. When requests
  arrive with `X-Forwarded-Proto: https` but FiGet would build `http://` addresses, it logs a warning once: current NuGet
  clients refuse `http://` resources under an `https://` source.
- **The proxy's body limit** must be at least `FiGet:Limits:MaxAssetSizeMB` (1024 MB by default), or large uploads are
  refused before they reach FiGet.

## Database and replicas

- **SQLite** for one container; **SQL Server** as soon as there is more than one replica. Set
  `FiGet:Database:ExpectedReplicas` to the replica count: FiGet refuses to start with SQLite above 1.
- Migrations run at start under the database's migration lock, so every replica may start at once.
- Retention, pruning the audit log and sweeping abandoned uploads run on one replica at a time, which takes a lease in the
  database. Nothing to configure.
- Rate limits are counted per replica.

## The key ring

Sign-in cookies, antiforgery tokens and the stored client secrets of sign-in providers are protected by a key ring that
lives in the database, so every replica shares it.

- **`FiGet:DataProtection:MasterKey`**: 32 random bytes in base64 (`openssl rand -base64 32`) that encrypt that key ring,
  from a secret (on OpenShift an ESO-synced Secret). Without it the start logs a warning; with more than one replica FiGet
  refuses to start without it.
- Setting it later encrypts the keys already stored, in place: sessions and provider secrets stay valid.
- **Keep it.** Without it, or with another one, the ring cannot be read: every session ends and provider secrets have to
  be entered again.

## Storage

- **One volume for FiGet's storage** (`FiGet:Storage:Root`), ReadWriteMany when there is more than one replica.
- **`FiGet:Storage:TempPath`** on that volume or on a scratch volume, not on the container's own writable layer: uploads
  and packages downloaded from upstreams are buffered there while they are checked, and an installer is large.
- **Shares for asset directories** under one folder, `FiGet:Assets:SharesRoot`. See [Asset directories](asset-directories.md).
- **A UNC path works as the storage root** on Windows (`\\server\share\figet`), tried with pushes, downloads and asset
  uploads.
- **Admin → Storage check** lists stored files no package, symbol or asset names - left by a failed delete or a stop
  between a file and its record - and removes them on request. Files written in the last hour are never counted.

## Secrets

- **Upstream credentials** are environment variables whose names start with `FIGET_UPSTREAM_` (`FIGET_UPSTREAM_GALLERY`);
  an upstream names the variable, never the secret. Any other name is refused. A bare key is sent with the user name
  `figet`; for an upstream that checks the user too, write the secret as `user:password`. Only administrators set an upstream's
  address and credential; feed managers edit its name, patterns and switch, and add the known public galleries.
- **`FiGet:Auth:BootstrapAdminToken`**, if used, a long random value from a secret.
- **The database connection string** from a secret.
- A sign-in provider's client secret is entered on its page and stored encrypted with the key ring.

## Sign-in providers

- With a provider anyone can register at (Google, a multi-tenant Entra registration), set **Allowed email domains**, or
  turn **Make an account at a first sign-in** off and connect accounts from their profiles.
- Keep one local super admin: `/account/login/local` is the way in when the provider cannot be reached.

## Network

- **Allowed networks** per feed or asset directory, on its settings page, when a feed should only answer internal
  clients.
- **Fetch by URL** reaches the internet directly by default. Behind an egress proxy set `FiGet:RemoteFetch:Proxy` together
  with `FiGet:RemoteFetch:AllowedHosts`.
- **Anonymous rate limits** are per client address. Many clients behind one address, such as an office's NAT, can hit
  them: raise `FiGet:RateLimits:AnonymousRequestsPerMinute`, or give that client a key, since requests with a valid key
  are not limited. A request that sends no credential to a feed that needs one is not counted either: NuGet clients with
  stored credentials send each request once without them and answer the 401.

## The image

`ghcr.io/rousseauxy/figet:latest`, or a version tag such as `0.1.0`. One tag carries `linux/amd64` and `linux/arm64`,
built on a runner of each architecture. It runs as a non-root, arbitrary UID in group 0, listens on 8080, and writes
only to `/data` and `/tmp`.

## Backup and recovery

Two things hold the state, and a restore needs both from the same moment:

- **The database** — feeds, versions, accounts, keys, the audit log, and the key ring that protects sessions.
- **The storage root** — the package and symbol files, and the files of asset directories that are not folder-backed. A
  folder-backed directory is the share itself and is backed up wherever that share is.

Back the database up the way your provider expects (SQL Server backup; for SQLite, `sqlite3 figet.db ".backup …"`,
which is safe while FiGet runs — copying the file while it is being written is not). Restore both, start FiGet, and it
applies any migrations the newer binary needs. A database restored without its files leaves versions whose downloads
answer 404: **Admin → Storage check** lists files no row names, and pushing an identical package again restores its
file (for a version whose row is at least an hour old).

If the key ring is lost, everyone is signed out and has to sign in again; nothing else is damaged. Set
`FiGet__DataProtection__MasterKey` so the ring survives a rebuild of the database.

**When nobody can sign in any more** — the last administrator's account is locked, disabled or forgotten — set
`FiGet:Auth:Recovery:UserName` and `FiGet:Auth:Recovery:Password` and restart. That account is created if it does not
exist, enabled, unlocked, made super administrator, given that password, and asked to choose a new one at sign-in. It
runs on **every** start while the settings are there, and the log warns each time: remove them once you are back in.

## Health

`/health/live` answers when the process runs; `/health/ready` when the database answers too.
