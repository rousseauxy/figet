#Requires -Version 5.1
<#
.SYNOPSIS
    Phase 0 recording: drives Windows PowerShell 5.1 with PowerShellGet 2.2.5 and PackageManagement 1.4.8.1 (which
    brings NuGet provider 3.0.0.1) through the v2 scenarios of build plan section 7.2, via FiGet.Recorder.

.DESCRIPTION
    Run from Windows PowerShell 5.1 (powershell.exe), not PowerShell 7. Start FiGet.Recorder first, pointing at a
    reference server that has:
      - a PowerShell feed without connectors (default name 'curated'),
      - a PowerShell feed with a PowerShell Gallery connector (default name 'modules').

    Each scenario is labelled in the recorder, so every recorded exchange carries the scenario that caused it.
    A failing scenario is logged and the run continues; the outcome of each is written to summary.txt.

    Side effects, all undone at the end: two temporary repository registrations (PSRepositories.xml and the user's
    NuGet.Config, to which PowerShellGet 2.x also adds the repositories as package sources, are backed up and restored
    byte for byte) and a synthetic test module installed for the current user and uninstalled again.
    No other installed module is touched: every Install/Update/Uninstall names the synthetic module explicitly.

.PARAMETER BaselineModulePath
    Folder containing PowerShellGet\2.2.5 and PackageManagement\1.4.8.1 (the pinned fleet versions). It is put
    first on PSModulePath: PackageManagement discovers the PowerShellGet provider on PSModulePath, so importing
    the module by path alone leaves the inbox PowerShellGet 1.0.0.1 provider in charge, and every PowerShellGet
    2.x cmdlet then fails with "A parameter cannot be found that matches parameter name 'AllowPrereleaseVersions'".
    Note that PackageManagement 1.4.8.1 then selects its bundled NuGet provider 3.0.0.1 (which has a v3 client),
    not a separately installed 2.8.5.208.

.EXAMPLE
    powershell.exe -NoProfile -File tests\FiGet.Compat\Record-PowerShellGetV2.ps1 -Recorder http://127.0.0.1:5590 -ApiKey $key -BaselineModulePath D:\baseline\files
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Recorder,
    [Parameter(Mandatory)] [string] $ApiKey,
    [Parameter(Mandatory)] [string] $BaselineModulePath,
    [string] $CuratedFeed = 'curated',
    [string] $ProxyFeed = 'modules',
    [string] $TestModule = 'FiGetRecordingTest',
    [string] $GalleryModule = 'Microsoft.PowerShell.SecretManagement',
    [string] $GalleryOldVersion = '1.0.0',
    [string] $SummaryPath = (Join-Path $env:TEMP 'figet-record-psget-summary.txt'),
    # Folder with a NuGet.exe that PowerShellGet can pack with (4.1 or later) and that still pushes to plain HTTP (before 7.0).
    [string] $NuGetExeDirectory,
    # Adds scenarios with large gallery packages: a module with more than 40 versions (paging) and a meta-module
    # with dozens of exactly pinned dependencies, saved at an older version and then at the latest. Downloads
    # several hundred megabytes through the proxy feed.
    [switch] $IncludeLargePackages,
    [string] $PagingModule = 'Pester',
    [string] $PagingOldVersion = '4.10.1',
    [string] $MetaModule = 'Microsoft.Graph',
    [string] $MetaOldVersion = '2.30.0'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Recorder = $Recorder.TrimEnd('/')
$work = Join-Path $env:TEMP ('figet-record-psget-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$repoFile = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\PowerShell\PowerShellGet\PSRepositories.xml'
$repoBackup = "$repoFile.figet-backup"
$nugetConfigFile = Join-Path $env:APPDATA 'NuGet\NuGet.Config'
$nugetConfigBackup = "$nugetConfigFile.figet-backup"
$curatedRepo = 'FiGetRecCurated'
$proxyRepo = 'FiGetRecProxy'
New-Item -ItemType Directory -Path $work | Out-Null
Set-Content -Path $SummaryPath -Value "PowerShellGet v2 recording $(Get-Date -Format s)" -Encoding UTF8

function Set-Scenario([string] $Name) {
    Invoke-RestMethod -Method Post -Uri "$Recorder/_recorder/scenario?name=$Name" | Out-Null
    Write-Host "=== $Name"
}

function Invoke-Scenario([string] $Name, [scriptblock] $Body) {
    Set-Scenario $Name
    try {
        $result = & $Body *>&1 | Out-String
        Add-Content -Path $SummaryPath -Value "ok    $Name" -Encoding UTF8
        $clean = ($result -split "`r?`n" | Where-Object { $_.Trim() }) -join "`n"
        if ($clean) { Add-Content -Path $SummaryPath -Value ($clean -replace '(?m)^', '      ') -Encoding UTF8 }
    }
    catch {
        Add-Content -Path $SummaryPath -Value "ERROR $Name :: $($_.Exception.Message)" -Encoding UTF8
        Write-Warning "$Name failed: $($_.Exception.Message)"
    }
}

function New-Folder([string] $Name) {
    $path = Join-Path $work $Name
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    return $path
}

function New-TestModule([string] $Version, [string] $Prerelease) {
    $dir = Join-Path $work "src\$Version$Prerelease\$TestModule"
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Set-Content -Path (Join-Path $dir "$TestModule.psm1") -Value "function Get-FiGetRecording { '$Version$Prerelease' }"
    $manifest = @{
        Path              = (Join-Path $dir "$TestModule.psd1")
        RootModule        = "$TestModule.psm1"
        ModuleVersion     = $Version
        Author            = 'FiGet'
        Description       = 'Synthetic module for FiGet protocol recordings'
        FunctionsToExport = @('Get-FiGetRecording')
        Tags              = @('figet', 'recording')
        ProjectUri        = 'https://example.org/figet'
    }
    New-ModuleManifest @manifest
    if ($Prerelease) {
        # Windows PowerShell 5.1's manifest template has no Prerelease line to uncomment; PowerShellGet 2.x writes it.
        Update-ModuleManifest -Path $manifest.Path -Prerelease $Prerelease
        if ((Import-PowerShellDataFile $manifest.Path).PrivateData.PSData.Prerelease -ne $Prerelease) { throw "Prerelease '$Prerelease' was not written to $($manifest.Path)." }
    }
    return $dir
}

try {
    Copy-Item -Path $repoFile -Destination $repoBackup -Force
    if (Test-Path $nugetConfigFile) { Copy-Item -Path $nugetConfigFile -Destination $nugetConfigBackup -Force }

    $env:PSModulePath = "$BaselineModulePath;" + $env:PSModulePath

    # PowerShellGet 2.2.5 packs and pushes with the dotnet CLI whenever it finds one, and the NuGet 7 client in current
    # SDKs refuses to push to plain HTTP. Servers without an SDK use NuGet.exe (bootstrapped under LOCALAPPDATA), so
    # hide dotnet from this process to record what they do.
    $env:PATH = (($env:PATH -split ';') | Where-Object { $_ -and ($_ -notmatch '\\dotnet\\?$') }) -join ';'
    if (Get-Command dotnet -ErrorAction SilentlyContinue) { throw 'dotnet is still on PATH; PowerShellGet would publish with it.' }
    if ($NuGetExeDirectory) { $env:PATH = "$NuGetExeDirectory;" + $env:PATH }
    $nugetExe = Get-Command NuGet.exe -ErrorAction SilentlyContinue
    Add-Content -Path $SummaryPath -Value ("NuGet.exe: " + $(if ($nugetExe) { "$($nugetExe.Source) $($nugetExe.Version)" } else { 'none on PATH' })) -Encoding UTF8
    Import-Module PackageManagement -RequiredVersion 1.4.8.1 -Force
    Import-Module PowerShellGet -RequiredVersion 2.2.5 -Force
    $versions = "PowerShellGet $((Get-Module PowerShellGet).Version); PackageManagement $((Get-Module PackageManagement).Version); NuGet provider $((Get-PackageProvider -Name NuGet).Version)"
    Add-Content -Path $SummaryPath -Value $versions -Encoding UTF8
    Write-Host $versions

    $curatedUrl = "$Recorder/nuget/$CuratedFeed/"
    $proxyUrl = "$Recorder/nuget/$ProxyFeed/"

    Invoke-Scenario 'register-psrepository-curated' {
        Register-PSRepository -Name $curatedRepo -SourceLocation $curatedUrl -PublishLocation $curatedUrl -InstallationPolicy Trusted -ErrorAction Stop
        Get-PSRepository -Name $curatedRepo | Format-List Name, SourceLocation, PublishLocation | Out-String
    }
    Invoke-Scenario 'register-psrepository-proxy' {
        Register-PSRepository -Name $proxyRepo -SourceLocation $proxyUrl -PublishLocation $proxyUrl -InstallationPolicy Trusted -ErrorAction Stop
    }

    Invoke-Scenario 'find-module-missing' { Find-Module -Name $TestModule -Repository $curatedRepo -ErrorAction Stop }

    Invoke-Scenario 'publish-module-1.0.0' { Publish-Module -Path (New-TestModule '1.0.0' '') -Repository $curatedRepo -NuGetApiKey $ApiKey -ErrorAction Stop }
    Invoke-Scenario 'publish-module-1.1.0' { Publish-Module -Path (New-TestModule '1.1.0' '') -Repository $curatedRepo -NuGetApiKey $ApiKey -ErrorAction Stop }
    Invoke-Scenario 'publish-module-2.0.0-beta1' { Publish-Module -Path (New-TestModule '2.0.0' 'beta1') -Repository $curatedRepo -NuGetApiKey $ApiKey -ErrorAction Stop }
    Invoke-Scenario 'publish-module-duplicate' { Publish-Module -Path (New-TestModule '1.1.0' '') -Repository $curatedRepo -NuGetApiKey $ApiKey -ErrorAction Stop }

    Invoke-Scenario 'find-module-by-name' { Find-Module -Name $TestModule -Repository $curatedRepo -ErrorAction Stop | Format-Table Name, Version -AutoSize }
    Invoke-Scenario 'find-module-required-version' { Find-Module -Name $TestModule -RequiredVersion '1.0.0' -Repository $curatedRepo -ErrorAction Stop | Format-Table Name, Version -AutoSize }
    Invoke-Scenario 'find-module-all-versions' { Find-Module -Name $TestModule -AllVersions -Repository $curatedRepo -ErrorAction Stop | Format-Table Name, Version -AutoSize }
    Invoke-Scenario 'find-module-allow-prerelease' { Find-Module -Name $TestModule -AllowPrerelease -Repository $curatedRepo -ErrorAction Stop | Format-Table Name, Version -AutoSize }
    Invoke-Scenario 'find-module-all-versions-prerelease' { Find-Module -Name $TestModule -AllVersions -AllowPrerelease -Repository $curatedRepo -ErrorAction Stop | Format-Table Name, Version -AutoSize }
    Invoke-Scenario 'find-module-wildcard' { Find-Module -Name 'FiGetRec*' -Repository $curatedRepo -ErrorAction Stop | Format-Table Name, Version -AutoSize }
    Invoke-Scenario 'find-module-star' { Find-Module -Name '*' -Repository $curatedRepo -ErrorAction Stop | Format-Table Name, Version -AutoSize }
    Invoke-Scenario 'find-module-tag' { Find-Module -Tag 'recording' -Repository $curatedRepo -ErrorAction Stop | Format-Table Name, Version -AutoSize }
    Invoke-Scenario 'find-command' { Find-Command -Name 'Get-FiGetRecording' -Repository $curatedRepo -ErrorAction Stop | Format-Table Name, ModuleName, Version -AutoSize }

    Invoke-Scenario 'save-module-latest' { $to = New-Folder 'saved-latest'; Save-Module -Name $TestModule -Repository $curatedRepo -Path $to -ErrorAction Stop; (Get-ChildItem (Join-Path $to $TestModule)).Name }
    Invoke-Scenario 'save-module-required-version' { Save-Module -Name $TestModule -RequiredVersion '1.0.0' -Repository $curatedRepo -Path (New-Folder 'saved-100') -ErrorAction Stop }

    Invoke-Scenario 'install-module-required-version' { Install-Module -Name $TestModule -RequiredVersion '1.0.0' -Repository $curatedRepo -Scope CurrentUser -Force -ErrorAction Stop }
    Invoke-Scenario 'update-module' { Update-Module -Name $TestModule -ErrorAction Stop; (Get-InstalledModule -Name $TestModule -AllVersions | Select-Object -ExpandProperty Version) -join ', ' }
    Invoke-Scenario 'install-module-allow-prerelease' { Install-Module -Name $TestModule -AllowPrerelease -Repository $curatedRepo -Scope CurrentUser -Force -AllowClobber -ErrorAction Stop }

    Invoke-Scenario 'find-package-nuget-provider-all-versions' { Find-Package -Name $TestModule -ProviderName NuGet -Source $curatedUrl -AllVersions -ErrorAction Stop | Format-Table Name, Version -AutoSize }
    Invoke-Scenario 'install-package-nuget-provider' { Install-Package -Name $TestModule -ProviderName NuGet -Source $curatedUrl -Destination (New-Folder 'pkg') -Force -ErrorAction Stop | Format-Table Name, Version -AutoSize }

    Invoke-Scenario 'proxy-find-module-uncached' { Find-Module -Name $GalleryModule -Repository $proxyRepo -ErrorAction Stop | Format-Table Name, Version -AutoSize }
    Invoke-Scenario 'proxy-save-module-old-version' { Save-Module -Name $GalleryModule -RequiredVersion $GalleryOldVersion -Repository $proxyRepo -Path (New-Folder 'gallery-old') -ErrorAction Stop }
    Invoke-Scenario 'proxy-find-module-after-old-version-cached' { Find-Module -Name $GalleryModule -Repository $proxyRepo -ErrorAction Stop | Format-Table Name, Version, Repository -AutoSize }
    Invoke-Scenario 'proxy-find-module-all-versions-after-cache' { Find-Module -Name $GalleryModule -AllVersions -Repository $proxyRepo -ErrorAction Stop | Format-Table Name, Version -AutoSize }
    Invoke-Scenario 'proxy-save-module-latest' { $to = New-Folder 'gallery-latest'; Save-Module -Name $GalleryModule -Repository $proxyRepo -Path $to -ErrorAction Stop; (Get-ChildItem (Join-Path $to $GalleryModule)).Name }

    if ($IncludeLargePackages) {
        Invoke-Scenario 'paging-find-module-all-versions' { $all = @(Find-Module -Name $PagingModule -AllVersions -Repository $proxyRepo -ErrorAction Stop); "versions: $($all.Count)" }
        Invoke-Scenario 'paging-save-module-old-version' { Save-Module -Name $PagingModule -RequiredVersion $PagingOldVersion -Repository $proxyRepo -Path (New-Folder 'paging-old') -ErrorAction Stop }
        Invoke-Scenario 'paging-find-module-latest-after-cache' { Find-Module -Name $PagingModule -Repository $proxyRepo -ErrorAction Stop | Format-Table Name, Version -AutoSize }

        Invoke-Scenario 'meta-find-module-latest' { Find-Module -Name $MetaModule -Repository $proxyRepo -ErrorAction Stop | Format-Table Name, Version -AutoSize }
        Invoke-Scenario 'meta-save-module-old-version' {
            $to = New-Folder 'meta-old'
            Save-Module -Name $MetaModule -RequiredVersion $MetaOldVersion -Repository $proxyRepo -Path $to -ErrorAction Stop
            $saved = @(Get-ChildItem $to -Directory)
            "saved modules: $($saved.Count); versions: " + ((Get-ChildItem $to -Directory | ForEach-Object { (Get-ChildItem $_.FullName -Directory).Name } | Sort-Object -Unique) -join ', ')
        }
        Invoke-Scenario 'meta-find-module-latest-after-old-cached' { Find-Module -Name $MetaModule -Repository $proxyRepo -ErrorAction Stop | Format-Table Name, Version -AutoSize }
        Invoke-Scenario 'meta-find-dependency-after-old-cached' { Find-Module -Name "$MetaModule.Authentication" -Repository $proxyRepo -ErrorAction Stop | Format-Table Name, Version -AutoSize }
        Invoke-Scenario 'meta-save-module-latest-partially-cached' {
            $to = New-Folder 'meta-latest'
            Save-Module -Name $MetaModule -Repository $proxyRepo -Path $to -ErrorAction Stop
            $saved = @(Get-ChildItem $to -Directory)
            "saved modules: $($saved.Count); versions: " + ((Get-ChildItem $to -Directory | ForEach-Object { (Get-ChildItem $_.FullName -Directory).Name } | Sort-Object -Unique) -join ', ')
        }
    }
}
finally {
    Set-Scenario 'cleanup'
    try { Uninstall-Module -Name $TestModule -AllVersions -Force -ErrorAction SilentlyContinue } catch { }
    foreach ($base in @([Environment]::GetFolderPath('MyDocuments'))) {
        $left = Join-Path $base "WindowsPowerShell\Modules\$TestModule"
        if (Test-Path $left) { Remove-Item -Recurse -Force $left }
    }
    foreach ($name in $curatedRepo, $proxyRepo) {
        try { Unregister-PSRepository -Name $name -ErrorAction SilentlyContinue } catch { }
    }
    if (Test-Path $repoBackup) { Move-Item -Path $repoBackup -Destination $repoFile -Force }
    if (Test-Path $nugetConfigBackup) { Move-Item -Path $nugetConfigBackup -Destination $nugetConfigFile -Force }
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    Add-Content -Path $SummaryPath -Value "cleanup done" -Encoding UTF8
}
