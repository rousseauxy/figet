#Requires -Version 7.2
#Requires -Modules Microsoft.PowerShell.PSResourceGet
<#
.SYNOPSIS
    Phase 0 recording: drives PowerShell 7 with PSResourceGet against a reference server's v2 feed root, via
    FiGet.Recorder.

.DESCRIPTION
    PSResourceGet decides the protocol from the repository URI. This script registers the feed root as it is
    usually given for a v2 PowerShell feed (ending in the feed name, with a trailing slash) and records what
    PSResourceGet detects and sends. Every scenario is labelled in the recorder and its outcome written to the
    summary file. The temporary repository registration is always removed again; nothing is installed outside
    the script's temporary folder.

.PARAMETER ReadOnly
    Records against a server that cannot be published to, such as the PowerShell Gallery through a recorder started
    with --preserve-host false. Uses existing gallery modules instead of publishing synthetic ones.

.EXAMPLE
    pwsh -NoProfile -File tests/FiGet.Compat/Record-PSResourceGetV2.ps1 -Recorder http://127.0.0.1:5591 -Feed psrg -ApiKey $key

.EXAMPLE
    pwsh -NoProfile -File tests/FiGet.Compat/Record-PSResourceGetV2.ps1 -Recorder http://127.0.0.1:5593 -RepositoryPath api/v2 -ReadOnly
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Recorder,
    [string] $Feed,
    [string] $ApiKey,
    # Path under the recorder that is registered as the repository; default nuget/{Feed}/.
    [string] $RepositoryPath,
    [switch] $ReadOnly,
    [string] $GalleryModule = 'Microsoft.PowerShell.SecretManagement',
    [string] $GalleryDependent = 'Microsoft.PowerShell.SecretStore',
    [string] $PagingModule = 'Pester',
    [string] $TestModule = 'FiGetPSResourceGetTest',
    [string] $SummaryPath = (Join-Path ([IO.Path]::GetTempPath()) 'figet-record-psrg-summary.txt')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Recorder = $Recorder.TrimEnd('/')
$work = Join-Path ([IO.Path]::GetTempPath()) ('figet-record-psrg-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$repository = 'FiGetRecPSRG'
New-Item -ItemType Directory -Path $work | Out-Null
Set-Content -Path $SummaryPath -Value "PSResourceGet v2 recording $(Get-Date -Format s); PowerShell $($PSVersionTable.PSVersion); PSResourceGet $((Get-Module -ListAvailable Microsoft.PowerShell.PSResourceGet | Sort-Object Version -Descending | Select-Object -First 1).Version)"

function Invoke-Scenario([string] $Name, [scriptblock] $Body) {
    Invoke-RestMethod -Method Post -Uri "$Recorder/_recorder/scenario?name=$Name" | Out-Null
    Write-Host "=== $Name"
    try {
        $result = & $Body *>&1 | Out-String
        Add-Content -Path $SummaryPath -Value "ok    $Name"
        $clean = ($result -split "`r?`n" | Where-Object { $_.Trim() }) -join "`n"
        if ($clean) { Add-Content -Path $SummaryPath -Value ($clean -replace '(?m)^', '      ') }
    }
    catch {
        Add-Content -Path $SummaryPath -Value "ERROR $Name :: $($_.Exception.Message)"
        Write-Warning "$Name failed: $($_.Exception.Message)"
    }
}

function New-TestModule([string] $Version, [string] $Prerelease) {
    $dir = Join-Path $work "src/$Version$Prerelease/$TestModule"
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Set-Content -Path (Join-Path $dir "$TestModule.psm1") -Value "function Get-FiGetPSResourceGet { '$Version$Prerelease' }"
    $manifest = @{
        Path              = (Join-Path $dir "$TestModule.psd1")
        RootModule        = "$TestModule.psm1"
        ModuleVersion     = $Version
        Author            = 'FiGet'
        Description       = 'Synthetic module for FiGet protocol recordings'
        FunctionsToExport = @('Get-FiGetPSResourceGet')
        Tags              = @('figet', 'recording')
    }
    if ($Prerelease) { $manifest.Prerelease = $Prerelease }
    New-ModuleManifest @manifest
    return $dir
}

try {
    if (-not $RepositoryPath) {
        if (-not $Feed) { throw 'Pass -Feed or -RepositoryPath.' }
        $RepositoryPath = "nuget/$Feed/"
    }

    $feedUrl = "$Recorder/$($RepositoryPath.TrimStart('/'))"

    Invoke-Scenario 'register-psresourcerepository' {
        Register-PSResourceRepository -Name $repository -Uri $feedUrl -Trusted
        Get-PSResourceRepository -Name $repository | Format-List Name, Uri, ApiVersion
    }

    if ($ReadOnly) {
        Invoke-Scenario 'find-psresource-by-name' { Find-PSResource -Name $GalleryModule -Repository $repository | Format-Table Name, Version, Prerelease -AutoSize }
        Invoke-Scenario 'find-psresource-exact-version' { Find-PSResource -Name $GalleryModule -Version '1.0.0' -Repository $repository | Format-Table Name, Version -AutoSize }
        Invoke-Scenario 'find-psresource-version-range' { Find-PSResource -Name $GalleryModule -Version '[1.0.0, 1.1.0]' -Repository $repository | Format-Table Name, Version -AutoSize }
        Invoke-Scenario 'find-psresource-all-versions' { Find-PSResource -Name $GalleryModule -Version '*' -Prerelease -Repository $repository | Format-Table Name, Version, Prerelease -AutoSize }
        Invoke-Scenario 'find-psresource-prerelease' { Find-PSResource -Name $GalleryModule -Prerelease -Repository $repository | Format-Table Name, Version, Prerelease -AutoSize }
        Invoke-Scenario 'find-psresource-wildcard' { Find-PSResource -Name 'Microsoft.PowerShell.Secret*' -Repository $repository | Format-Table Name, Version -AutoSize }
        Invoke-Scenario 'find-psresource-tag' { Find-PSResource -Tag 'SecretManagement' -Repository $repository | Select-Object -First 5 | Format-Table Name, Version -AutoSize }
        Invoke-Scenario 'find-psresource-command' { Find-PSResource -CommandName 'Get-Secret' -Repository $repository | Select-Object -First 5 | Format-Table Names, ParentResource -AutoSize }
        Invoke-Scenario 'find-psresource-dependencies' { Find-PSResource -Name $GalleryDependent -IncludeDependencies -Repository $repository | Format-Table Name, Version -AutoSize }
        Invoke-Scenario 'find-psresource-many-versions' { $all = @(Find-PSResource -Name $PagingModule -Version '*' -Prerelease -Repository $repository); "versions: $($all.Count)" }
        Invoke-Scenario 'save-psresource-latest' { $to = Join-Path $work 'saved-latest'; New-Item -ItemType Directory $to | Out-Null; Save-PSResource -Name $GalleryModule -Repository $repository -Path $to -TrustRepository; (Get-ChildItem (Join-Path $to $GalleryModule)).Name }
        Invoke-Scenario 'save-psresource-exact-version' { $to = Join-Path $work 'saved-100'; New-Item -ItemType Directory $to | Out-Null; Save-PSResource -Name $GalleryModule -Version '1.0.0' -Repository $repository -Path $to -TrustRepository }
    }
    else {
        Invoke-Scenario 'find-psresource-missing' { Find-PSResource -Name $TestModule -Repository $repository }
        Invoke-Scenario 'publish-psresource-1.0.0' { Publish-PSResource -Path (New-TestModule '1.0.0' '') -Repository $repository -ApiKey $ApiKey }
        Invoke-Scenario 'publish-psresource-1.1.0' { Publish-PSResource -Path (New-TestModule '1.1.0' '') -Repository $repository -ApiKey $ApiKey }
        Invoke-Scenario 'publish-psresource-2.0.0-beta1' { Publish-PSResource -Path (New-TestModule '2.0.0' 'beta1') -Repository $repository -ApiKey $ApiKey }
        Invoke-Scenario 'publish-psresource-duplicate' { Publish-PSResource -Path (New-TestModule '1.1.0' '') -Repository $repository -ApiKey $ApiKey }

        Invoke-Scenario 'find-psresource-by-name' { Find-PSResource -Name $TestModule -Repository $repository | Format-Table Name, Version, Prerelease -AutoSize }
        Invoke-Scenario 'find-psresource-prerelease' { Find-PSResource -Name $TestModule -Prerelease -Repository $repository | Format-Table Name, Version, Prerelease -AutoSize }
        Invoke-Scenario 'find-psresource-version-range' { Find-PSResource -Name $TestModule -Version '[1.0.0, 2.0.0)' -Repository $repository | Format-Table Name, Version -AutoSize }
        Invoke-Scenario 'find-psresource-all-versions' { Find-PSResource -Name $TestModule -Version '*' -Prerelease -Repository $repository | Format-Table Name, Version, Prerelease -AutoSize }
        Invoke-Scenario 'find-psresource-wildcard' { Find-PSResource -Name 'FiGetPSResource*' -Repository $repository | Format-Table Name, Version -AutoSize }
        Invoke-Scenario 'find-psresource-tag' { Find-PSResource -Tag 'recording' -Repository $repository | Format-Table Name, Version -AutoSize }
        Invoke-Scenario 'find-psresource-command' { Find-PSResource -CommandName 'Get-FiGetPSResourceGet' -Repository $repository | Format-Table Names, ParentResource -AutoSize }

        Invoke-Scenario 'save-psresource-latest' { $to = Join-Path $work 'saved-latest'; New-Item -ItemType Directory $to | Out-Null; Save-PSResource -Name $TestModule -Repository $repository -Path $to -TrustRepository; (Get-ChildItem (Join-Path $to $TestModule)).Name }
        Invoke-Scenario 'save-psresource-exact-version' { $to = Join-Path $work 'saved-100'; New-Item -ItemType Directory $to | Out-Null; Save-PSResource -Name $TestModule -Version '1.0.0' -Repository $repository -Path $to -TrustRepository }
        Invoke-Scenario 'save-psresource-prerelease' { $to = Join-Path $work 'saved-pre'; New-Item -ItemType Directory $to | Out-Null; Save-PSResource -Name $TestModule -Prerelease -Repository $repository -Path $to -TrustRepository; (Get-ChildItem (Join-Path $to $TestModule)).Name }
    }
}
finally {
    Invoke-RestMethod -Method Post -Uri "$Recorder/_recorder/scenario?name=cleanup" | Out-Null
    Unregister-PSResourceRepository -Name $repository -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    Add-Content -Path $SummaryPath -Value 'cleanup done'
}
