# Compatibility scripts

These drive real clients against a running FiGet instance (build plan §7.2). They are not part of
`dotnet test`: they need the clients installed, and some need Windows.

Start an instance, for example:

```powershell
$env:FiGet__Auth__BootstrapAdminToken = 'a-long-random-secret'
$env:FiGet__Feeds__0__Name = 'modules'
$env:FiGet__Feeds__0__AnonymousRead = 'true'
dotnet run --project src/FiGet.Web
```

Then run, from PowerShell 7:

```powershell
./tests/FiGet.Compat/Invoke-DotnetCliCompat.ps1     -FeedUrl http://localhost:5555/nuget/modules/v3/index.json -ApiKey $env:FiGet__Auth__BootstrapAdminToken
./tests/FiGet.Compat/Invoke-PSResourceGetCompat.ps1 -FeedUrl http://localhost:5555/nuget/modules/v3/index.json -ApiKey $env:FiGet__Auth__BootstrapAdminToken
```

Each script uses unique package names, so it can run repeatedly against the same feed, and cleans up
its temporary files and registrations.

| Script | Client | Protocol | Phase |
|---|---|---|---|
| `Invoke-DotnetCliCompat.ps1` | dotnet CLI (NuGet 7) | v3 | 1 |
| `Invoke-PSResourceGetCompat.ps1` | PowerShell 7 + PSResourceGet | v3 | 1 |
| `Record-PowerShellGetV2.ps1` (phase 0 recording, run through FiGet.Recorder) | Windows PowerShell 5.1 + PowerShellGet 2.2.5 / PackageManagement 1.4.8.1 (NuGet provider 3.0.0.1) | v2 | 0 |
| nuget.exe | | v2 and v3 | 2 |

The NuGet 7 client refuses plain-HTTP sources unless the source sets `allowInsecureConnections`; the
dotnet script writes such a `nuget.config` for itself. Production instances run behind HTTPS.
