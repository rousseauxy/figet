# PowerShell clients

FiGet answers both NuGet protocols, so Windows PowerShell 5.1 and PowerShell 7 both work against the same feed. What
follows is how to register each one, and the client behaviours worth knowing before they cost an afternoon. They are
client behaviours: nothing on this page is something the server can decide for you.

## Register a feed

**Windows PowerShell 5.1 (PowerShellGet 2.x)**

```powershell
Register-PSRepository -Name modules -SourceLocation https://packages.example/nuget/modules/ `
    -PublishLocation https://packages.example/nuget/modules/ -InstallationPolicy Trusted
```

**PowerShell 7 (PSResourceGet)**

```powershell
Register-PSResourceRepository -Name modules -Uri https://packages.example/nuget/modules/api/v2 -Trusted
```

Register PSResourceGet at **`/api/v2`, with no trailing slash**. It decides which protocol a repository speaks from the
text of the URL, not by asking the server: a URL ending in `/api/v2` is v2, one ending in `index.json` is v3, and
anything else - including `…/api/v2/` with a slash - is an unknown type, after which every `Find-PSResource` and
`Install-PSResource` fails with "not a known repository type". A repository already registered at the feed root can be
corrected without re-registering:

```powershell
Set-PSResourceRepository modules -ApiVersion V2
```

Use v2 for PSResourceGet even though FiGet serves v3 as well. Over v3 the client refuses wildcards, `-Tag` and
`-CommandName`, and its exact-version install matches the file name by substring, so asking for `2.2.4` can install
2.2.4.1. Over v2 those all behave. Keep v3 for `dotnet` and `nuget.exe`.

## Credentials

- A key is passed as `-ApiKey`, a user name and password as `-Credential`. **Do not pass both.** With a key present the
  client does not retry after a refusal, so a wrong key fails without ever offering the credential. Either is enough on
  its own; any user name works with a FiGet key as the password, and an
  [API access token from your identity provider](api-access-tokens.md) can be the password too.
- A credential kept in SecretManagement must be a `PSCredential`, not a plain string; a plain string is sent in a way
  FiGet answers 401 to.
- PSResourceGet has no proxy or client-certificate support of its own: set `HTTPS_PROXY` for the process.

## Behaviours that surprise people

| What you see | Why | What to do |
|---|---|---|
| `Find-PSResource -Name *` lists every package twice | The client asks for modules and scripts with the same query against any v2 server that is not the public gallery, and prints both answers | `Find-PSResource -Name * \| Sort-Object Name -Unique` |
| `-Prerelease` returns a stable version | It means "the absolute latest", not "the latest prerelease" | Ask for a version range, or `-Version *` and sort yourself |
| A .NET library fails to install with a dependency named like a framework | The client misreads an empty dependency group, which library packages have and modules do not | `-SkipDependencyCheck`, or install libraries with `dotnet` |
| A module installs but will not load | The module's manifest spells its version differently from the package (`2.1` packed as 2.1.0), and the folder is named after the reported version | FiGet reports the manifest's spelling, as the public gallery does; update to a version served by FiGet |

## Publishing

```powershell
# PowerShell 7
Publish-PSResource -Path ./MyModule -Repository modules -ApiKey $key

# Windows PowerShell 5.1
Publish-Module -Path ./MyModule -Repository modules -NuGetApiKey $key
```

Both need a key or account with **Publish** on the feed. A feed used for PowerShell modules refuses anything that is not
a module or a script; see [the overview](index.md).
