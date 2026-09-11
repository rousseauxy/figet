#Requires -Version 7.2
<#
.SYNOPSIS
    Drives the real dotnet CLI against a running FiGet v3 feed: push (with symbols), add, restore from an
    empty package cache, and search. Exits non-zero on the first failed assertion.

.EXAMPLE
    ./Invoke-DotnetCliCompat.ps1 -FeedUrl http://127.0.0.1:5555/nuget/modules/v3/index.json -ApiKey $token
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $FeedUrl,
    [Parameter(Mandatory)] [string] $ApiKey
)

$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) ("figet-compat-dotnet-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$id = 'FiGet.Compat.Dotnet' + [guid]::NewGuid().ToString('N').Substring(0, 8)
New-Item -ItemType Directory -Path $work | Out-Null

function Assert-That([bool] $Condition, [string] $Message, [string] $Output = '') {
    if (-not $Condition) { throw "FAILED: $Message`n$Output" }
    Write-Host "ok  $Message"
}

function Invoke-Dotnet {
    $output = & dotnet @args 2>&1 | Out-String
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

try {
    $config = Join-Path $work 'nuget.config'
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="figet" value="$FeedUrl" allowInsecureConnections="true" />
  </packageSources>
</configuration>
"@ | Set-Content -Path $config -Encoding utf8

    $lib = Join-Path $work 'lib'
    Invoke-Dotnet new classlib -n $id -o $lib | Out-Null
    foreach ($version in '1.0.0', '1.1.0-beta.1') {
        $pack = Invoke-Dotnet pack $lib -c Release -o (Join-Path $work 'out') "-p:PackageVersion=$version" '-p:IncludeSymbols=true' '-p:SymbolPackageFormat=snupkg' '-p:Authors=FiGet' '-p:Description=Compat'
        Assert-That ($pack.ExitCode -eq 0) "pack $version" $pack.Output
    }

    foreach ($version in '1.0.0', '1.1.0-beta.1') {
        $push = Invoke-Dotnet nuget push (Join-Path $work "out/$id.$version.nupkg") -s figet -k $ApiKey --configfile $config
        Assert-That ($push.ExitCode -eq 0) "push $version" $push.Output
        Assert-That ($push.Output -match 'symbolpublish') "symbols for $version pushed alongside"
    }

    $duplicate = Invoke-Dotnet nuget push (Join-Path $work "out/$id.1.0.0.nupkg") -s figet -k $ApiKey --configfile $config
    Assert-That ($duplicate.ExitCode -ne 0 -and $duplicate.Output -match '409') 'duplicate push is 409'
    $skip = Invoke-Dotnet nuget push (Join-Path $work "out/$id.1.0.0.nupkg") -s figet -k $ApiKey --configfile $config --skip-duplicate
    Assert-That ($skip.ExitCode -eq 0) 'duplicate push with --skip-duplicate succeeds'
    $badKey = Invoke-Dotnet nuget push (Join-Path $work "out/$id.1.0.0.nupkg") -s figet -k 'figet_wrong' --configfile $config
    Assert-That ($badKey.ExitCode -ne 0 -and $badKey.Output -match '403') 'push with a wrong key is 403'

    $consumer = Join-Path $work 'consumer'
    Invoke-Dotnet new console -n Consumer -o $consumer | Out-Null
    Copy-Item $config $consumer
    $env:NUGET_PACKAGES = Join-Path $work 'packages'
    Push-Location $consumer
    try {
        $add = Invoke-Dotnet add package $id
        Assert-That ($add.ExitCode -eq 0 -and $add.Output -match "version '1\.0\.0'") 'add package picks the latest stable version'
        $addPre = Invoke-Dotnet add package $id --prerelease
        Assert-That ($addPre.ExitCode -eq 0 -and $addPre.Output -match "version '1\.1\.0-beta\.1'") 'add package --prerelease picks the prerelease'

        Remove-Item -Recurse -Force (Join-Path $consumer 'obj'), $env:NUGET_PACKAGES -ErrorAction SilentlyContinue
        $restore = Invoke-Dotnet restore -v n
        Assert-That ($restore.ExitCode -eq 0 -and $restore.Output -match "Installed $([regex]::Escape($id)) 1\.1\.0-beta\.1 from") 'restore installs from FiGet with an empty cache'
    }
    finally {
        Pop-Location
        Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    }

    # The table output wraps long ids across rows, so assert on the JSON output instead.
    $search = Invoke-Dotnet package search $id --source figet --configfile $config --prerelease --format json
    $hits = @(($search.Output | ConvertFrom-Json).searchResult.packages | Where-Object id -eq $id)
    Assert-That ($search.ExitCode -eq 0 -and $hits.Count -eq 1 -and $hits[0].latestVersion -eq '1.1.0-beta.1') 'package search finds the package with its latest version' $search.Output

    Write-Host "dotnet CLI compatibility: all checks passed ($id)"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
