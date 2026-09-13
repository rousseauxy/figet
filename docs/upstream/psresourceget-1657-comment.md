Adding a reproduction with public packages in support of this fix, plus one remaining collision the new matcher still lets through.

### Reproduction on 1.2.0

**Environment:** PSResourceGet **1.2.0** on PowerShell 7.6.5 (Windows). Also seen on 1.1.0.1 under Windows PowerShell 5.1, and on macOS.

Against a NuGet **v3** feed that serves PowerShellGet's published versions (not tested against the PowerShell Gallery directly, which PSResourceGet reaches over v2 by default):

```powershell
Save-PSResource -Repository <v3-feed> -Name PowerShellGet -Version 2.2.4 -Path $dir -TrustRepository
Select-String -Path "$dir/PowerShellGet/2.2.4/PowerShellGet.psd1" -Pattern ModuleVersion
```

| requested | folder created | `ModuleVersion` inside |
|---|---|---|
| 2.2.4 | 2.2.4 | **2.2.4.1** |
| 2.2.5 | 2.2.5 | **2.2.5.1** |
| 2.2.3 | 2.2.3 | 2.2.3 (control, no longer sibling) |

`2.2.4` and `2.2.4.1` are **both listed**, so this has nothing to do with unlisted versions. That is an easy wrong conclusion when the colliding version happens to be unlisted, as `2.2.5.1` is. Resolution is correct (the prompt and the folder both say 2.2.4); only the payload is wrong, which you only notice by opening the installed manifest.

### The PR fixes these

I compiled `GetPackageContentUrlForVersion` from this branch and fed it flat-container URLs in descending order:

| requested | entries | selected |
|---|---|---|
| 2.2.4 | 2.2.4.1, 2.2.4 | 2.2.4 ✅ |
| 2.2.5 | 2.2.5.1, 2.2.5 | 2.2.5 ✅ |
| 0.0.4 | 0.0.4-beta, 0.0.4 | 0.0.4 ✅ (also #2030) |
| 2.5.1 | **3.2.5.1**, 2.5.1 | **3.2.5.1** ❌ |

### Remaining collision: the file-name suffix check

For `.../pkg/3.2.5.1/pkg.3.2.5.1.nupkg` the path-segment check correctly rejects `3.2.5.1`. The file-name check then accepts it, because `pkg.3.2.5.1.nupkg` ends with `.2.5.1.nupkg`:

```csharp
if (segment.EndsWith($".{normalizedVersion}.nupkg", StringComparison.OrdinalIgnoreCase))
```

Any version whose text ends in `.{requested}` collides the same way, and because it is higher it sorts first. It is the same bug the PR removes, moved from the start of the string to the end. Rarer, but a silent wrong install again.

A possible fix is to pass the package name in (both `InstallHelper` and `InstallHelperAsync` already have `packageName`) and compare the whole file name:

```csharp
if (segment.Equals($"{packageName}.{normalizedVersion}.nupkg", StringComparison.OrdinalIgnoreCase))
```

or to drop the file-name check whenever a version path segment is present. A test in the style of the new file:

```powershell
It 'Should not select a url whose file name merely ends with the requested version' {
    $responses = @(
        "$packageBaseAddress/3.2.5.1/test_module.3.2.5.1.nupkg",
        "$packageBaseAddress/2.5.1/test_module.2.5.1.nupkg"
    )
    $url = [Microsoft.PowerShell.PSResourceGet.UtilClasses.TestHooks]::SelectV3PackageContentUrl($responses, '2.5.1')
    $url | Should -BeExactly "$packageBaseAddress/2.5.1/test_module.2.5.1.nupkg"
}
```
