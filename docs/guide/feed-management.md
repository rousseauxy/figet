# Feeds and directories from a script

An administrator makes feeds and asset directories on **Admin → Feeds** and **Admin → Assets**. This page is about
doing the same from a script: setting up a new server the same way every time, or giving a new team its own
directory, without anybody at a browser.

## What it takes

The same key as for [service tokens from a script](service-tokens.md): a service token **with the admin scope**, sent
as `X-ApiKey`, the password of Basic authentication, or a Bearer token. A super administrator makes that one on the
tokens page.

The API follows the same rules as the pages, because both call the same code. What the create form refuses, the API
refuses, and every change lands in the audit log under the same name.

## Make a feed

```powershell
$admin = @{ 'X-ApiKey' = $adminKey }
$feed = Invoke-RestMethod -Method Post "https://packages.example/api/management/feeds" -Headers $admin `
    -ContentType 'application/json' -Body (@{
        name    = 'modules'
        purpose = 'powershell'   # any, powershell, nuget or chocolatey
        gallery = $true          # proxy the PowerShell Gallery from the start
    } | ConvertTo-Json)

$feed.url   # https://packages.example/nuget/modules/v3/index.json
```

| Field | Meaning |
|---|---|
| `name` | Letters, digits, dot, dash or underscore, starting with a letter or digit, at most 64. Required. |
| `kind` | `package` (the default) or `directory`. |
| `purpose` | `any` (the default), `powershell`, `nuget` or `chocolatey`. A push of the wrong kind of package is refused. |
| `gallery` | With a purpose: the matching public gallery becomes the feed's upstream, so it is a proxy feed from the first request. |
| `anonymousRead` | Anyone can read without a key. Off by default. |
| `allowOverwrite` | Pushing an existing version replaces it, instead of `409`. |
| `onDelete` | `unlist` (the default) or `delete`: what a client's delete does. |

## Make an asset directory

```powershell
Invoke-RestMethod -Method Post "https://packages.example/api/management/feeds" -Headers $admin `
    -ContentType 'application/json' -Body (@{
        name          = 'installers'
        kind          = 'directory'
        anonymousRead = $true     # anyone downloads a file whose path they know
        anonymousList = $false    # but only key holders browse the folders
    } | ConvertTo-Json)
```

On a server with a shares mount (`FiGet:Assets:SharesRoot`), a directory can serve one of the shares as it is:

```powershell
-Body (@{ name = 'intune'; kind = 'directory'; folder = @{ share = 'intune'; inside = 'scripts'; writes = $false } } | ConvertTo-Json)
```

`share` is the name of a folder directly under the mount, `inside` a folder within it. Anything that is not on the
mount right now is refused; a path is never accepted, and the answers never show one. Move a directory later with
`PUT .../feeds/{name}/folder`, and back to FiGet's own storage with an empty `share`.

## Change, rename, delete

```powershell
# Only what is sent changes.
Invoke-RestMethod -Method Patch "https://packages.example/api/management/feeds/modules" -Headers $admin `
    -ContentType 'application/json' -Body (@{ anonymousRead = $true; allowedNetworks = @('10.0.0.0/8') } | ConvertTo-Json)

# The old name keeps working for clients that still use it.
Invoke-RestMethod -Method Post "https://packages.example/api/management/feeds/modules/rename" -Headers $admin `
    -ContentType 'application/json' -Body (@{ name = 'ps-modules'; keepOldName = $true } | ConvertTo-Json)

# The name is repeated on purpose: a script whose variable came out empty deletes nothing.
Invoke-RestMethod -Method Delete "https://packages.example/api/management/feeds/ps-modules?confirm=ps-modules" -Headers $admin
```

`allowedNetworks` is a list of addresses and CIDR ranges the feed may be reached from; `[]` opens it to any address
again. If one entry is not valid, nothing in the request is applied. A delete takes everything the feed or directory
holds, files included.

`GET .../feeds` lists them all and `GET .../feeds/{name}` shows one with its alternate names.

## Upstreams

A package feed asks its upstreams, in order, for what it does not hold. Adding the first one makes it a proxy feed.

```powershell
$upstreams = "https://packages.example/api/management/feeds/modules/upstreams"

# A public gallery by name: it supplies the address and the protocol.
Invoke-RestMethod -Method Post $upstreams -Headers $admin -ContentType 'application/json' `
    -Body (@{ name = 'gallery'; gallery = 'PowerShell Gallery' } | ConvertTo-Json)

# A server of your own, asked only for your own packages, with a password from the environment.
$internal = Invoke-RestMethod -Method Post $upstreams -Headers $admin -ContentType 'application/json' -Body (@{
        name          = 'internal'
        url           = 'https://nuget.internal.example/v3/index.json'
        allow         = '^Contoso\.'
        credentialRef = 'FIGET_UPSTREAM_INTERNAL'
    } | ConvertTo-Json)

# Ask it first, then switch it off for a while.
Invoke-RestMethod -Method Post "$upstreams/$($internal.id)/move" -Headers $admin -ContentType 'application/json' `
    -Body (@{ direction = 'up' } | ConvertTo-Json)
Invoke-RestMethod -Method Patch "$upstreams/$($internal.id)" -Headers $admin -ContentType 'application/json' `
    -Body (@{ enabled = $false } | ConvertTo-Json)
```

| Field | Meaning |
|---|---|
| `name` | Unique within the feed. Required. |
| `gallery` | `nuget.org`, `PowerShell Gallery` or `Chocolatey community`, instead of `url` and `protocol`. |
| `url`, `protocol` | Where the upstream is, and `v3` (the default) or `v2`. An http or https address; an address written as an IP on the link-local or reserved blocks is refused. A host name is yours to vouch for: it is not checked. |
| `allow`, `deny` | Regular expressions, one per line, matched anywhere in the package id and ignoring case: only ids that match, or never these. Anchor with `^` and `$` for a prefix or a whole id. |
| `credentialRef` | The name of an environment variable holding the password, starting with `FIGET_UPSTREAM_`. The password itself is never sent to FiGet's API. `""` removes it. |
| `enabled` | Off: the upstream stays configured but is not asked. |

An upstream is addressed by its `id`, from the list or from the answer that made it. `DELETE .../upstreams/{id}`
removes it with the listings cached from it; the packages already cached stay in the feed.

## Not here yet

Who may do what on a feed, retention and the install instructions are still set on the pages.

## What is refused, and how

| Answer | When |
|---|---|
| `401` | No key was sent, or what was sent as a Basic password or Bearer token is not a valid key. |
| `403` | A key that is not valid sent as `X-ApiKey`; a valid key that is not a service token with the admin scope. |
| `400` | The body is not JSON of the shape above, a value is not one of those listed, a share is not on the mount, or a delete without `?confirm=`. The `error` field says which. |
| `404` | No feed or directory has that name, or alternate name. |
| `409` | The name is taken by a feed, a directory, an alternate name, or another upstream of the feed; a folder that cannot be chosen here; an upstream on an asset directory. |
