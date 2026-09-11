#Requires -Version 7.2
<#
.SYNOPSIS
    Phase 0 recording: drives nuget.exe against a reference server's v2 NuGet feed, via FiGet.Recorder.

.DESCRIPTION
    Packs synthetic packages with nuget.exe itself (no .NET SDK needed) and runs push, list, search, install,
    delete through the recorder. nuget.exe 7.x refuses plain-HTTP pushes, so pass a nuget.exe from before 7.0
    when recording over HTTP. Uses its own nuget.config and its own global packages folder in a temporary
    folder, so neither the user's NuGet.Config nor the user's package cache is touched.

.EXAMPLE
    pwsh -NoProfile -File tests/FiGet.Compat/Record-NuGetExeV2.ps1 -Recorder http://127.0.0.1:5591 -Feed nugetv2 -ApiKey $key -NuGetExe C:\tools\nuget.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Recorder,
    [Parameter(Mandatory)] [string] $Feed,
    [Parameter(Mandatory)] [string] $ApiKey,
    [Parameter(Mandatory)] [string] $NuGetExe,
    [string] $TestPackage = 'FiGet.NuGetExe.Test',
    [string] $SummaryPath = (Join-Path ([IO.Path]::GetTempPath()) 'figet-record-nugetexe-summary.txt')
)

$ErrorActionPreference = 'Stop'
$Recorder = $Recorder.TrimEnd('/')
$work = Join-Path ([IO.Path]::GetTempPath()) ('figet-record-nugetexe-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
$feedUrl = "$Recorder/nuget/$Feed/"
$config = Join-Path $work 'nuget.config'
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="figet" value="$feedUrl" protocolVersion="2" allowInsecureConnections="true" />
  </packageSources>
</configuration>
"@ | Set-Content -Path $config -Encoding utf8
$version = (& $NuGetExe help 2>&1 | Select-Object -First 1)
$env:NUGET_PACKAGES = Join-Path $work 'global-packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $work 'http-cache'
Set-Content -Path $SummaryPath -Value "nuget.exe v2 recording $(Get-Date -Format s); $version"

function Invoke-Scenario([string] $Name, [string[]] $Arguments, [switch] $ExpectFailure, [switch] $NoConfigFile) {
    Invoke-RestMethod -Method Post -Uri "$Recorder/_recorder/scenario?name=$Name" | Out-Null
    Write-Host "=== $Name"
    # 'search' rejects -ConfigFile; it gets the feed URL directly instead of the source name. The arguments go into a
    # list rather than a splatted array: a one-element array from an if-expression unrolls to a string, and
    # splatting a string passes it character by character.
    $all = [System.Collections.Generic.List[string]]::new()
    $all.AddRange($Arguments)
    $all.Add('-NonInteractive')
    if (-not $NoConfigFile) { $all.Add('-ConfigFile'); $all.Add($config) }
    $output = & $NuGetExe $all 2>&1 | Out-String
    $status = if ($LASTEXITCODE -eq 0) { 'ok   ' } elseif ($ExpectFailure) { 'fail*' } else { 'ERROR' }
    Add-Content -Path $SummaryPath -Value "$status $Name (exit $LASTEXITCODE)"
    $clean = ($output -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 8) -join "`n"
    if ($clean) { Add-Content -Path $SummaryPath -Value ($clean -replace '(?m)^', '      ') }
}

function New-Package([string] $Version) {
    $dir = Join-Path $work "src/$Version"
    New-Item -ItemType Directory -Path (Join-Path $dir 'content') -Force | Out-Null
    Set-Content -Path (Join-Path $dir 'content/readme.txt') -Value "FiGet recording $Version"
    @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
  <metadata>
    <id>$TestPackage</id>
    <version>$Version</version>
    <authors>FiGet</authors>
    <description>Synthetic package for FiGet protocol recordings</description>
    <tags>figet recording</tags>
  </metadata>
</package>
"@ | Set-Content -Path (Join-Path $dir "$TestPackage.nuspec") -Encoding utf8
    & $NuGetExe pack (Join-Path $dir "$TestPackage.nuspec") -OutputDirectory (Join-Path $work 'out') -NoPackageAnalysis -NonInteractive | Out-Null
    return (Join-Path $work "out/$TestPackage.$Version.nupkg")
}

try {
    $v1 = New-Package '1.0.0'
    $v2 = New-Package '1.1.0'
    $pre = New-Package '2.0.0-beta1'

    Invoke-Scenario 'push-1.0.0' @('push', $v1, '-Source', 'figet', '-ApiKey', $ApiKey)
    Invoke-Scenario 'push-1.1.0' @('push', $v2, '-Source', 'figet', '-ApiKey', $ApiKey)
    Invoke-Scenario 'push-2.0.0-beta1' @('push', $pre, '-Source', 'figet', '-ApiKey', $ApiKey)
    Invoke-Scenario 'push-duplicate' @('push', $v2, '-Source', 'figet', '-ApiKey', $ApiKey) -ExpectFailure
    Invoke-Scenario 'push-wrong-key' @('push', $v1, '-Source', 'figet', '-ApiKey', 'figet_wrong') -ExpectFailure

    Invoke-Scenario 'list-by-name' @('list', $TestPackage, '-Source', 'figet')
    Invoke-Scenario 'list-all-versions-prerelease' @('list', $TestPackage, '-Source', 'figet', '-AllVersions', '-PreRelease')
    Invoke-Scenario 'search' @('search', 'FiGet.NuGetExe', '-Source', $feedUrl, '-PreRelease') -NoConfigFile
    Invoke-Scenario 'install-latest' @('install', $TestPackage, '-Source', 'figet', '-OutputDirectory', (Join-Path $work 'pk-latest'), '-NoHttpCache')
    Invoke-Scenario 'install-exact-version' @('install', $TestPackage, '-Version', '1.0.0', '-Source', 'figet', '-OutputDirectory', (Join-Path $work 'pk-100'), '-NoHttpCache')
    Invoke-Scenario 'install-prerelease' @('install', $TestPackage, '-PreRelease', '-Source', 'figet', '-OutputDirectory', (Join-Path $work 'pk-pre'), '-NoHttpCache')
    Invoke-Scenario 'delete-1.0.0' @('delete', $TestPackage, '1.0.0', '-Source', 'figet', '-ApiKey', $ApiKey)
    Invoke-Scenario 'list-after-delete' @('list', $TestPackage, '-Source', 'figet', '-AllVersions')
}
finally {
    Invoke-RestMethod -Method Post -Uri "$Recorder/_recorder/scenario?name=cleanup" | Out-Null
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    Add-Content -Path $SummaryPath -Value 'cleanup done'
}
