# Service tokens from a script

A service token is a key that belongs to no person: the key of a build agent, a deployment host, a scheduled job. An
administrator makes one on **Admin → Tokens**. This page is about doing the same from a script, so that giving a new
host its own key, and taking it away again, needs nobody at a browser.

## What it takes

A service token **with the admin scope**, sent like any other key: `X-ApiKey`, the password of Basic authentication,
or a Bearer token. A super administrator makes that one on the tokens page. Keep it where your automation keeps its
other secrets; it is the key that makes keys.

What that key can do over the API is deliberately less than what a person can do on the page:

- It makes, lists and revokes tokens that **read, push and delete**.
- It never makes a token with the **admin** scope, and never revokes one. A key that can make admin keys can make more
  of itself; that stays with a super administrator on the page.
- It does not see or revoke **personal keys**. Those belong to their owners.

## Make a token

```powershell
$admin = @{ 'X-ApiKey' = $adminKey }
$made = Invoke-RestMethod -Method Post "https://packages.example/api/management/tokens" -Headers $admin `
    -ContentType 'application/json' -Body (@{
        name          = 'build-agent-07'
        scopes        = @('read', 'push')
        feed          = 'modules'      # leave out for every feed
        expiresInDays = 90             # leave out for a token that does not end
    } | ConvertTo-Json)

$made.token   # the secret. Shown in this answer and never again.
```

| Field | Meaning |
|---|---|
| `name` | What the token is called in lists and in the logs. Required. |
| `scopes` | One or more of `read`, `push`, `delete`. Pushing and deleting include reading. Required. |
| `feed` | A feed or an asset directory to limit the token to. Without it the token applies to all of them. |
| `expiresInDays` | 1 to 3650. Without it the token does not end. |

The answer is `201` with the token: its `id`, `name`, `prefix` (the first characters, to recognise it by), `scopes`,
`feed`, `expiresUtc` and the secret in `token`. Store the secret straight away; FiGet keeps only its hash.

## List and revoke

```powershell
$tokens = Invoke-RestMethod "https://packages.example/api/management/tokens" -Headers $admin
$tokens | Where-Object { $_.active -and $_.daysLeft -le 14 } | Format-Table id, name, feed, daysLeft

Invoke-RestMethod -Method Delete "https://packages.example/api/management/tokens/$($made.id)" -Headers $admin
```

Each entry has `active` (not revoked and not ended), `lastUsedUtc`, and `daysLeft` for a token that ends: the whole
days it has left, rounded up. A revoked token is refused from the next request on. Revoking one that is already
revoked is not an error.

## Replacing a key without a gap

Two tokens can be valid at the same time, so a replacement never needs a moment where neither works:

1. Make the new token, with the same scopes and feed.
2. Give it to whatever used the old one.
3. Watch `lastUsedUtc` of the old one stop moving, then revoke it.

## Tokens that are about to end

A token with an expiry stops working without telling whoever uses it. FiGet says so ahead of time, twice: **fourteen
days** and **three days** before the end.

- The tokens page marks the token: *ends in 5 days*.
- The audit log gets a `token.expiring` entry, and the server's own log a warning, once at each distance.
- The API's list counts down in `daysLeft`, for a job of your own that replaces keys before they end.

The check runs once a day (`FiGet:Jobs:TokenExpiryWatch`). It covers personal keys as well as service tokens.

## What is refused, and how

| Answer | When |
|---|---|
| `401` | No key was sent, or what was sent as a Basic password or Bearer token is not a valid key. |
| `403` | A key sent as `X-ApiKey` that is not valid; a valid key that is not a service token with the admin scope; a request that asks for the admin scope; revoking a token that has it. |
| `400` | The body is not what the table above describes. The `error` field says which part. |
| `404` | Revoking an id that is not a service token. |

Making and revoking a token are in the audit log as `token.create` and `token.revoke`, by the name of the key that
did it.
