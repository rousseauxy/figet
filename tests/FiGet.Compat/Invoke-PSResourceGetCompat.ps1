#Requires -Version 7.2
#Requires -Modules Microsoft.PowerShell.PSResourceGet
<#
.SYNOPSIS
    Drives PSResourceGet against a running FiGet v3 feed: register (auto-detects v3), publish, find, save,
    import. Registers a temporary repository and always removes it again.

.NOTES
    PSResourceGet itself refuses wildcard names and tag searches on every v3 repository; those belong to the
    v2 tests.

.EXAMPLE
    ./Invoke-PSResourceGetCompat.ps1 -FeedUrl http://127.0.0.1:5555/nuget/modules/v3/index.json -ApiKey $token
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $FeedUrl,
    [Parameter(Mandatory)] [string] $ApiKey
)

$ErrorActionPreference = 'Stop'
$suffix = [guid]::NewGuid().ToString('N').Substring(0, 8)
$repository = "FiGetCompat$suffix"
$module = "FiGetCompatModule$suffix"
$work = Join-Path ([IO.Path]::GetTempPath()) "figet-compat-psrg-$suffix"

function Assert-That([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "FAILED: $Message" }
    Write-Host "ok  $Message"
}

try {
    $source = Join-Path $work "src/$module"
    New-Item -ItemType Directory -Path $source -Force | Out-Null
    Set-Content -Path (Join-Path $source "$module.psm1") -Value 'function Get-FiGetCompat { ''compat'' }'
    New-ModuleManifest -Path (Join-Path $source "$module.psd1") -RootModule "$module.psm1" -ModuleVersion '1.2.3' `
        -Author 'FiGet' -Description 'FiGet compatibility module' -FunctionsToExport 'Get-FiGetCompat' -Tags 'figet', 'compat'

    Register-PSResourceRepository -Name $repository -Uri $FeedUrl -Trusted
    $registered = Get-PSResourceRepository -Name $repository
    Assert-That ("$($registered.ApiVersion)" -eq 'V3') 'the repository is detected as v3'

    Publish-PSResource -Path $source -Repository $repository -ApiKey $ApiKey
    Write-Host 'ok  publish'

    $duplicate = $null
    try { Publish-PSResource -Path $source -Repository $repository -ApiKey $ApiKey } catch { $duplicate = $_ }
    Assert-That ($null -ne $duplicate -and "$duplicate" -match '409') 'publishing the same version again is refused with 409'

    $found = Find-PSResource -Name $module -Repository $repository
    Assert-That ($found.Name -eq $module -and "$($found.Version)" -eq '1.2.3') 'find by name'

    $save = Join-Path $work 'saved'
    New-Item -ItemType Directory -Path $save | Out-Null
    Save-PSResource -Name $module -Repository $repository -Path $save -TrustRepository
    Assert-That (Test-Path (Join-Path $save "$module/1.2.3/$module.psd1")) 'save writes the module'

    Import-Module (Join-Path $save $module) -Force
    Assert-That ((Get-FiGetCompat) -eq 'compat') 'the saved module imports and runs'
    Remove-Module $module -Force

    Write-Host "PSResourceGet compatibility: all checks passed ($module)"
}
finally {
    Unregister-PSResourceRepository -Name $repository -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
