# FiGet sign-in with Microsoft Entra ID

> **Note — Status 2026-09-13**
>
> Sign-in through OpenID Connect is built and verified end to end against Authentik. Entra ID goes through the same
> code path; FiGet has no Entra-specific code. This guide follows FiGet's implementation and Microsoft's
> documentation, and has not yet been confirmed against a real Entra tenant.

## What FiGet expects from any provider

- A **confidential client** (client id and secret), **authorization code flow** with PKCE, the answer in the query
  string.
- Scopes `openid profile email`.
- A stable `sub` claim. FiGet links a provider identity to an account by provider and `sub`, never by name or email.
- Redirect URI `{FiGet base URL}/signin-oidc/{slug}`. The provider's page in FiGet shows the exact value.

A new account's user name comes from the configured claim (default `preferred_username`, which is the UPN in Entra),
or else from the email address.

**A first sign-in is refused** when its user name or email address already belongs to a FiGet account. Nothing is
created and nothing is joined. That person signs in with their local account first, then uses
**Profile → Sign-in providers → Connect**.

## 1. App registration

Entra admin centre → **Identity → Applications → App registrations → New registration**.

| Field | Value |
|---|---|
| Name | `FiGet` (or one per environment: `FiGet test`, ...) |
| Supported account types | **Accounts in this organizational directory only** (single tenant) |
| Redirect URI | Platform **Web**, `https://<figet-host>/signin-oidc/entra` |

For a test run on your own machine, add `http://localhost:<port>/signin-oidc/entra` as a second Web redirect URI.
Entra accepts plain HTTP only for `localhost`.

### Authentication

Under **Authentication** (in the newer view: **Authentication → Settings**), leave everything off:

| Setting | Value | Why |
|---|---|---|
| Implicit grant: Access tokens | **off** | FiGet uses the authorization code flow with PKCE and never asks for tokens in the redirect |
| Implicit grant: ID tokens | **off** | Same. Turned on, it only allows a flow that puts tokens in the browser's address bar |
| Front-channel logout URL | empty | Signing out of FiGet ends the FiGet session only |
| Allow public client flows | **off** | FiGet is a confidential client with a secret |

After creating it, note on **Overview**:

- **Application (client) ID**: FiGet's *Client id*
- **Directory (tenant) ID**: part of the issuer URL below

### Client secret

**Certificates & secrets → Client secrets → New client secret.** Copy the **Value**, not the Secret ID; Entra shows
it once. The longest lifetime is 24 months, so note the expiry date. When you make a new secret, paste it on the
FiGet provider page; it applies at the next sign-in, with no restart.

### Token configuration

**Token configuration → Add optional claim → ID → `email`**, and accept the prompt to add the Microsoft Graph `email`
permission. Without it Entra often sends no `email`, and FiGet's duplicate-account check can then only compare user
names.

### API permissions

The default `User.Read` plus `openid`, `profile` and `email` (Microsoft Graph, delegated) is all FiGet needs. If your
tenant does not allow user consent, press **Grant admin consent for &lt;tenant&gt;**; otherwise the first sign-in
stops at a consent screen an ordinary user cannot get past.

### Expose an API: only for access tokens

For sign-in, leave **Expose an API** empty. Package clients and scripts use a FiGet API key (**Profile → API keys**, or
service tokens under **Admin → Tokens**).

To let applications call FiGet's API with an Entra access token instead of a stored key, the same registration also
exposes an API, gives app roles the *Applications* member type, and asks for version 2 tokens. That's set up in
[API access tokens](api-access-tokens.md#entra-id-an-application-calling-figet).

## 2. Who may sign in, and app roles

### Restrict the application (recommended)

**Enterprise applications → FiGet → Properties → Assignment required? = Yes.** Only users and groups assigned under
**Users and groups** can then sign in at all. Assigning groups (rather than individual users) needs Entra ID P1 or
higher.

### App roles to FiGet groups

App roles are the better fit for feed permissions:

- they arrive in the ID token as a `roles` claim with readable values;
- they contain only the roles of *this* application;
- unlike the groups claim, they do not hit the 200-group overage limit, where Entra leaves the groups out and sends a
  link to Microsoft Graph instead.

1. **App registrations → FiGet → App roles → Create app role**, allowed member types *Users/Groups*, for example:

    | Display name | Value | Meaning in FiGet |
    |---|---|---|
    | FiGet readers | `FiGet.Readers` | Read on private feeds |
    | FiGet publishers | `FiGet.Publishers` | Publish on a module feed |
    | FiGet feed managers | `FiGet.Managers` | Manage a feed |

2. **Enterprise applications → FiGet → Users and groups → Add user/group**: choose the user or security group and the
   role.
3. In FiGet, create a group per role (**Admin → Authentication → Groups**), and grant each group its level on the
   feeds in the feed's **Access** panel.
4. On each FiGet group's page, **Provider groups → Link**: provider *Entra ID*, group at the provider = the role
   **value** (`FiGet.Publishers`).
5. On the provider's page in FiGet, set **Groups claim** to `roles`.

At every sign-in through Entra, FiGet makes the memberships that Entra gave the account match the `roles` claim.
Members added by hand are left alone. A role removed in Entra takes effect at that person's next sign-in, not
immediately.

> **Warning — App roles give feed access, not FiGet admin rights**
>
> By design, a provider's groups or roles never make someone a FiGet admin or super admin. Roles are set in FiGet
> itself, under **Admin → Authentication → Users**. An admin role in Entra therefore has no effect; don't create one.

### Security groups instead of app roles

Possible, but not recommended. **Token configuration → Add groups claim → Security groups** (better: *Groups assigned
to the application*, which also avoids the overage). Entra then sends group **object IDs**, not names: paste the
object ID as the provider group name in FiGet, and set the groups claim to `groups`.

## 3. The provider in FiGet

**Admin → Authentication → Providers → Add a provider** (super admin):

| Field | Value |
|---|---|
| Button name | `Microsoft`, or your organisation's name |
| Slug | `entra` (must match the redirect URI) |
| Issuer URL | `https://login.microsoftonline.com/<tenant-id>/v2.0` |
| Client id | Application (client) ID |
| Client secret | the secret **Value** |
| Scopes | `openid profile email` |
| User name claim | `preferred_username` |
| Groups claim | `roles` (or empty to leave memberships alone) |
| Allowed email domains | empty, or your organisation's domains, one per line |
| Make an account at a first sign-in | on, to let people sign in straight away; off, to connect only accounts an admin made |
| Enabled | on |

Use the tenant-specific issuer, never `common` or `organizations`: FiGet checks the token's issuer against the
discovery document, and the multi-tenant document has a `{tenantid}` placeholder that never matches.

**The issuer is still `login.microsoftonline.com` for a v2 registration.** The v2.0 endpoint lives on that same host;
the `/v2.0` at the end of the issuer URL is what selects it. The manifest's `requestedAccessTokenVersion` doesn't matter
for sign-in: FiGet reads the ID token, and the v2.0 endpoint always issues v2 ID tokens. It does matter for
[API access tokens](api-access-tokens.md), which must be version 2.

**A provider added on the page starts with *Make an account at a first sign-in* off.** Leave it off and nobody gets an
account by signing in: an admin makes the account, and its owner connects Entra ID from **Profile**. Turn it on to let
everyone Entra ID lets in get an account at their first sign-in. With **Allowed email domains** set, a first sign-in also
needs a verified address in one of them.

People who already have a local FiGet account: sign in locally, then **Profile → Connect** Entra ID.

The sign-in page shows provider buttons with a link to the local form, or buttons only; a super admin chooses on the
Providers page. `/account/login/local` keeps working either way, so keep one local super admin for when Entra is
unreachable.

## 4. Checks and troubleshooting

- **Discovery answers:** `https://login.microsoftonline.com/<tenant-id>/v2.0/.well-known/openid-configuration` returns
  JSON whose `issuer` is exactly the issuer URL above. The FiGet host must be able to reach
  `login.microsoftonline.com` and `graph.microsoft.com` (user info), through your egress proxy if it has one.
- **"Signing in with the provider did not work":** the reason is in FiGet's log and audit log as
  `signin.external.failed`, with the provider's error. Entra errors carry an `AADSTS` code:
    - `AADSTS50011`: redirect URI mismatch (scheme, host, slug, trailing slash).
    - `AADSTS7000215`: wrong secret (the Secret ID was pasted instead of the Value, or it expired).
    - `AADSTS65001`: consent missing; grant admin consent.
    - `AADSTS50105`: the user is not assigned while assignment is required.
- **Behind a reverse proxy or ingress:** set `FiGet:PublicBaseUrl` to the public `https://` address. FiGet builds the
  redirect URI from it, so it matches what is registered even when FiGet itself sees plain HTTP.
- **"There is no account for you yet":** the provider does not make accounts at a first sign-in. Turn *Make an account
  at a first sign-in* on for it, or make the account and let its owner connect the provider from the profile.
- **"This provider signs in only people with a verified email address in the domains an administrator allowed":** the
  address is not in *Allowed email domains*, or the provider marks it unverified.
- **"An account with this user name or email already exists":** by design, see above. Sign in locally and connect
  from the profile.
- **Roles not applied:** check that the provider's groups claim is `roles`, that the link on the FiGet group uses the
  role *value*, and sign in again. Memberships are refreshed at sign-in only. To see what the ID token contains, add
  `https://jwt.ms` as a temporary redirect URI and sign in there with a test user; remove it afterwards.
- **User info and `sub`:** FiGet also reads Microsoft Graph's user info endpoint and requires its `sub` to equal the ID
  token's. Microsoft documents them as the same; if the log says the subjects differ, that is the cause.
