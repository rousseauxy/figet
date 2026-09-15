# FiGet sign-in with Authentik

> **Note — Status 2026-09-13**
>
> Verified against Authentik 2026.8: signing in, and connecting an existing FiGet account from its profile. Group
> mapping follows the same code as the rest and is tested against an in-process OpenID provider, not yet against
> Authentik groups.

## What FiGet expects from any provider

- A **confidential client** (client id and secret), **authorization code flow** with PKCE, the answer in the query
  string.
- Scopes `openid profile email`.
- ID tokens signed with a key published by the provider, so FiGet can check them: RSA, RSA-PSS or ECDSA (Authentik's
  default, RS256, is one of them). A token signed with a shared secret is refused.
- Redirect URI `{FiGet base URL}/signin-oidc/{slug}`. The provider's page in FiGet shows the exact value.

A new account's user name comes from the configured claim (default `preferred_username`, which is the Authentik
username), or else from the email address.

**A first sign-in is refused** when its user name or email address already belongs to a FiGet account. Nothing is
created and nothing is joined. That person signs in with their local account first, then uses
**Profile → Sign-in providers → Connect**.

## 1. Application and provider in Authentik

Admin interface → **Applications → Applications → Create with provider**.

**Application**

| Field | Value |
|---|---|
| Name | `FiGet` |
| Slug | `figet` (part of the issuer URL) |
| Launch URL | `https://<figet-host>` (optional: puts FiGet on the user's Authentik dashboard) |

**Provider**: type **OAuth2/OpenID Provider**.

| Field | Value |
|---|---|
| Authorization flow | `default-provider-authorization-implicit-consent`, or the explicit-consent flow if users should confirm |
| Client type | **Confidential** |
| Client ID / Client Secret | generated; copy both for FiGet |
| Redirect URIs | **Strict**, `https://<figet-host>/signin-oidc/authentik` |
| Signing key | a certificate, for example `authentik Self-signed Certificate` |

Under **Advanced protocol settings**, keep the default scope mappings `openid`, `email` and `profile`, and the subject
mode *Based on the User's hashed ID*. That subject stays the same when a username or email changes, which is what FiGet
links on.

> **Warning — Choose a signing key**
>
> Without one, Authentik signs ID tokens with the client secret (HS256), which FiGet does not accept. Sign-in then
> fails after the Authentik login screen.

> **Warning — Created through the API or a blueprint? Set the grant types**
>
> The admin interface fills in the allowed grant types. A provider created through the API, a blueprint or
> `ak shell` can end up with none, and every sign-in then fails with `invalid_request`, "The request is otherwise
> malformed"; Authentik's server log says `Invalid grant_type for provider`. Allow `authorization_code`.

After saving, open the provider: **OpenID Configuration Issuer** shows the issuer URL, for example
`https://auth.example.com/application/o/figet/`.

## 2. Who may sign in

By default every Authentik user may open an application. To restrict FiGet, open the application →
**Policy / Group / User Bindings → Bind existing policy/group/user**, and bind a group such as `FiGet Users`. Only its
members can then sign in; anyone else is stopped by Authentik with "Permission denied".

## 3. Authentik groups to FiGet groups (optional)

Authentik's default `profile` scope sends a `groups` claim with the names of the groups the user is in.

1. In FiGet, create a group per role you want (**Admin → Authentication → Groups**), and grant each its level on the
   feeds in the feed's **Access** panel.
2. On each FiGet group's page, **Provider groups → Link**: provider *Authentik*, group at the provider = the Authentik
   group's **name**, for example `FiGet Publishers`.
3. On the provider's page in FiGet, set **Groups claim** to `groups`.

At every sign-in through Authentik, FiGet makes the memberships that Authentik gave the account match the `groups`
claim. Members added by hand are left alone. A change in Authentik takes effect at that person's next sign-in.

> **Warning — Authentik groups give feed access, not FiGet admin rights**
>
> By design, a provider's groups never make someone a FiGet admin or super admin. Roles are set in FiGet itself,
> under **Admin → Authentication → Users**.

## 4. The provider in FiGet

**Admin → Authentication → Providers → Add a provider** (super admin):

| Field | Value |
|---|---|
| Button name | `Authentik`, or what your users call their sign-in |
| Slug | `authentik` (must match the redirect URI) |
| Issuer URL | the provider's **OpenID Configuration Issuer** |
| Client id | Client ID |
| Client secret | Client Secret |
| Scopes | `openid profile email` |
| User name claim | `preferred_username` |
| Groups claim | `groups` (or empty to leave memberships alone) |
| Allowed email domains | empty, or your organisation's domains, one per line |
| Make an account at a first sign-in | on, to let people sign in straight away; off, to connect only accounts an admin made |
| Enabled | on |

**A provider added on the page starts with *Make an account at a first sign-in* off.** Leave it off and nobody gets an
account by signing in: an admin makes the account, and its owner connects Authentik from **Profile**. Turn it on to let
everyone Authentik lets in get an account at their first sign-in. With **Allowed email domains** set, a first sign-in also
needs a verified address in one of them.

People who already have a local FiGet account: sign in locally, then **Profile → Connect** Authentik.

The sign-in page shows provider buttons with a link to the local form, or buttons only; a super admin chooses on the
Providers page. `/account/login/local` keeps working either way, so keep one local super admin for when Authentik is
unreachable.

The **API access tokens** section of the same form lets service accounts call the API with an Authentik token instead
of a FiGet key; leave it off for sign-in alone. See [API access tokens](api-access-tokens.md#authentik).

## 5. Checks and troubleshooting

- **Discovery answers:** `<issuer>.well-known/openid-configuration` returns JSON whose `issuer` is the issuer URL. The
  FiGet host must be able to reach Authentik at that address.
- **"Signing in with the provider did not work":** the reason is in FiGet's log and audit log as
  `signin.external.failed`, with the provider's error. Authentik's own server log names the cause in more detail.
    - `invalid_request`, "otherwise malformed": the grant types, see above.
    - Authentik shows "The request fails due to a missing, invalid, or mismatching redirection URI": the registered
      redirect URI differs from FiGet's (scheme, host, slug, trailing slash).
    - Authentik shows "Permission denied": the user is not in a group bound to the application.
    - A failure after the Authentik login screen, about the token's signature: no signing key on the provider.
- **Behind a reverse proxy:** set `FiGet:PublicBaseUrl` to the public `https://` address. FiGet builds the redirect URI
  from it, so it matches what is registered even when FiGet itself sees plain HTTP.
- **"There is no account for you yet":** the provider does not make accounts at a first sign-in. Turn *Make an account
  at a first sign-in* on for it, or make the account and let its owner connect the provider from the profile.
- **"This provider signs in only people with a verified email address in the domains an administrator allowed":** the
  address is not in *Allowed email domains*, or the provider marks it unverified.
- **"An account with this user name or email already exists":** by design, see above. Sign in locally and connect
  from the profile.
- **Groups not applied:** check that the provider's groups claim is `groups`, that the link on the FiGet group uses the
  Authentik group's exact name, and sign in again. Memberships are refreshed at sign-in only.
