# API access tokens

> **Note — Status 2026-09-15**
>
> Built and tested against a test issuer that signs real tokens. Not yet tried with a real Entra ID, Authentik, GitLab
> or GitHub Actions token; this guide follows FiGet's implementation and each vendor's documentation.

FiGet's API (feeds, asset directories, the packages API) accepts two kinds of credential:

- **FiGet API keys**: personal keys and service tokens, made in FiGet. They work until they are revoked or expire.
- **Access tokens** from an identity provider or CI system that a super admin trusts. The pipeline or application
  fetches a new token when it runs, so there's no stored key to leak or rotate, and a leaked token stops working
  within the hour.

Both go in the same places, so no client needs anything new: the `X-NuGet-ApiKey` or `X-ApiKey` header (what
`-ApiKey` and `nuget push -k` send), the password of Basic authentication, or `Authorization: Bearer`. Access tokens are
for the API only; people still sign in to the pages as before.

## What a token may do

A token has no FiGet account. It may do what the **FiGet groups linked to its groups claim** may do, the same
[provider group links](sign-in-entra-id.md#app-roles-to-figet-groups) sign-ins use:

- the highest level those groups have on the feed or directory;
- **at most Publish** (push, upload, unlist, delete), even when a linked group has Manage;
- never anything admin.

Links and grants are read on every request, so removing either takes effect immediately, even for a token already
issued. A token linked to no group is refused with 403.

## What FiGet checks

| Check | Rule |
|---|---|
| Issuer | The token's `iss` is the issuer URL of a provider with **Accept access tokens** on |
| Signature | Signed with one of the keys the issuer publishes, with RSA, RSA-PSS or ECDSA. Shared-secret (HS256) and unsigned tokens are refused |
| Audience | One of the provider's **Audiences** |
| Lifetime | Not expired, and issued for no more than 24 hours in total |
| Kind | An access token: a sign-in's ID token is refused |
| Required claims | Every `name=value` rule in **Required claims** |

FiGet fetches the issuer's discovery document and signing keys from `<issuer>/.well-known/openid-configuration`, so
the FiGet host must reach the issuer. Keys are fetched again when the issuer rolls them over.

Every refusal is in the audit log as `token.refused`, with the provider and the reason (`wrong audience`, `expired`,
`required claim ref_protected missing or different`, ...). Use is logged as `token.external.used`, once an hour per
calling application.

## The provider in FiGet

**Admin → Authentication → Providers** (super admin). A provider people sign in with gets the **API access tokens**
section filled in. A CI system nobody signs in with is added as its own provider, with **Enabled** off and the client
id and secret left empty.

| Field | Value |
|---|---|
| Issuer URL | The issuer exactly as tokens carry it in `iss` |
| Groups claim | The claim whose values are linked to FiGet groups: `roles` (Entra ID), `groups` (Authentik), `project_path` (GitLab), `repository` (GitHub Actions) |
| Accept access tokens from this issuer on the API | On |
| Audiences | What the token's `aud` must be, one per line |
| Required claims | Optional, one `name=value` per line |

Then, on each FiGet group's page, **Provider groups → Link** the value from the groups claim, and grant the group its
level on the feeds.

> **Warning — A shared issuer needs an audience of its own and group links**
>
> Anyone with a project on gitlab.com, or a repository on GitHub, can get a token from the same issuer. FiGet's
> audience and the group links are what separate your pipelines from theirs: use an audience nobody else would ask for
> (FiGet's own address), link only your projects, and on a public GitLab or GitHub add a required claim for your
> group or organisation.

## Entra ID: an application calling FiGet

For a script, a service or an Azure pipeline with its own app registration or managed identity.

**In FiGet's app registration** (the one from [sign-in with Entra ID](sign-in-entra-id.md)):

1. **Expose an API → Application ID URI → Add**, keep `api://<FiGet client id>`.
2. **App roles**: give the roles that should work for applications the member type **Both (Users/Groups +
   Applications)**, for example `FiGet.Publishers`.
3. **Manifest**: set `requestedAccessTokenVersion` to `2` (under `api` in the current manifest format). Without it
   Entra issues version 1 tokens, whose issuer is `https://sts.windows.net/<tenant-id>/`, and FiGet refuses them as
   coming from an untrusted issuer.

**In the calling application's registration**: **API permissions → Add a permission → My APIs → FiGet → Application
permissions**, choose the role, then **Grant admin consent**. For a managed identity, assign the app role to its service
principal instead.

**In FiGet**, on the Entra ID provider:

| Field | Value |
|---|---|
| Accept access tokens | On |
| Audiences | FiGet's **Application (client) ID**: version 2 tokens carry the client id as `aud`, not the `api://` URI |
| Groups claim | `roles`, as for sign-in |

Link the role value (`FiGet.Publishers`) to a FiGet group, as for sign-in.

**Getting a token and publishing:**

```powershell
$token = (Invoke-RestMethod -Method Post "https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/token" -Body @{
    grant_type    = "client_credentials"
    client_id     = "<calling application id>"
    client_secret = $env:CALLER_SECRET
    scope         = "api://<FiGet client id>/.default"
}).access_token

Publish-PSResource -Path .\MyModule -Repository FiGet -ApiKey $token
```

The request log names the caller by its `azp` claim, the calling application's id.

## GitLab CI

GitLab issues a signed token to every job that asks for one (`id_tokens`, GitLab 15.7 and later). With a
self-managed GitLab, the issuer is its address.

**In FiGet**, a provider for GitLab:

| Field | Value |
|---|---|
| Issuer URL | `https://gitlab.example.org` (or `https://gitlab.com`) |
| Client id / secret | Empty |
| Enabled (sign-ins) | Off |
| Groups claim | `project_path` |
| Accept access tokens | On |
| Audiences | FiGet's address, for example `https://figet.example.org` |
| Required claims | `ref_protected=true`, so only protected branches and tags publish; on gitlab.com also `namespace_path=<your group>` |

Link each project allowed to publish, as `group/project`, to a FiGet group with Publish on the feed.

**In `.gitlab-ci.yml`:**

```yaml
publish:
  id_tokens:
    FIGET_TOKEN:
      aud: https://figet.example.org
  script:
    - pwsh -c 'Publish-PSResource -Path ./MyModule -Repository FiGet -ApiKey $env:FIGET_TOKEN'
```

The token lives as long as the job and is logged by its `sub`, for example
`project_path:tools/module-builder:ref_type:branch:ref:main`.

## GitHub Actions

**In FiGet**, a provider with issuer `https://token.actions.githubusercontent.com`, no client, sign-ins off, groups
claim `repository`, audience FiGet's address, and the required claim `repository_owner=<your organisation>`. Link each
repository (`org/repo`) to a group. Add `ref_protected=true` to publish only from protected branches.

**In the workflow:**

```yaml
permissions:
  id-token: write
steps:
  - name: Publish
    shell: pwsh
    run: |
      $url = "$env:ACTIONS_ID_TOKEN_REQUEST_URL&audience=https://figet.example.org"
      $token = (Invoke-RestMethod $url -Headers @{ Authorization = "Bearer $env:ACTIONS_ID_TOKEN_REQUEST_TOKEN" }).value
      Publish-PSResource -Path ./MyModule -Repository FiGet -ApiKey $token
```

## Authentik

Authentik signs access tokens as JWTs for an OAuth2 provider, with the application's client id as the audience. On the
Authentik provider in FiGet: **Accept access tokens** on, **Audiences** = the client id, **Groups claim** = `groups`.
A token for a service account comes from Authentik's token endpoint with the `client_credentials` grant. Which claims
the token carries depends on the provider's scope mappings, so check once that `groups` is in it (see below).

## Troubleshooting

- **401 or "The API key or access token is invalid"**: the reason is in the audit log under `token.refused`. Check the
  issuer (a trailing slash doesn't matter, anything else does), the audience, and, with Entra ID,
  `requestedAccessTokenVersion`.
- **403 "The token does not grant this operation"**: the token is valid but not linked to a group with enough rights
  on that feed. Check the groups claim name and the link's value.
- **To see what a token contains**, decode its middle part on your own machine instead of pasting it into a website:

    ```powershell
    $p = $token.Split('.')[1].Replace('-', '+').Replace('_', '/'); $p += '=' * ((4 - $p.Length % 4) % 4)
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p))
    ```
