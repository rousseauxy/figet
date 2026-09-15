using FiGet.Domain.Entities;

namespace FiGet.Domain.Packages;

/// <summary>What a package holds, as far as its files tell: the only way a server can tell a module from a Chocolatey package.</summary>
public enum PackageContentKind
{
    /// <summary>Files, but none that says what reads them: tools or content only, as a portable Chocolatey package can be.</summary>
    Other,

    /// <summary>No files beyond the nuspec: a meta package that only names dependencies.</summary>
    MetadataOnly,

    /// <summary>Assemblies, MSBuild files, analyzers, or a package type only the .NET tooling reads.</summary>
    DotNet,

    /// <summary>A module manifest <c>{id}.psd1</c> or a script <c>{id}.ps1</c> at the root, as PowerShellGet packs them.</summary>
    PowerShell,

    /// <summary>A <c>chocolateyInstall.ps1</c>, <c>chocolateyUninstall.ps1</c> or <c>chocolateyBeforeModify.ps1</c> script.</summary>
    Chocolatey,
}

/// <summary>A package's content kind and the file that decided it, for a refusal to name.</summary>
public sealed record PackageContent(PackageContentKind Kind, string Evidence)
{
    private static readonly string[] ChocolateyScripts = ["chocolateyinstall.ps1", "chocolateyuninstall.ps1", "chocolateybeforemodify.ps1"];

    private static readonly string[] DotNetFolders = ["lib/", "ref/", "runtimes/", "build/", "buildtransitive/", "buildmultitargeting/", "analyzers/", "contentfiles/"];

    private static readonly string[] DotNetPackageTypes = ["DotnetTool", "Template", "MSBuildSdk", "DotnetPlatform", "DotnetCliTool"];

    /// <summary>
    /// Decides from the archive's file names and the nuspec's package types. Chocolatey's scripts are looked for first: a
    /// Chocolatey package that installs a module carries the module too. Then a module, which may ship assemblies of its own.
    /// </summary>
    public static PackageContent Classify(string id, IEnumerable<string> files, IReadOnlyList<string> packageTypes)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(packageTypes);
        var paths = files
            .Select(f => f.Replace('\\', '/').TrimStart('/'))
            .Where(f => !IsPackagingFile(f))
            .ToList();

        if (paths.FirstOrDefault(f => ChocolateyScripts.Contains(f[(f.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase)) is { } script)
        {
            return new(PackageContentKind.Chocolatey, script);
        }

        if (paths.FirstOrDefault(f => f.Equals(id + ".psd1", StringComparison.OrdinalIgnoreCase) || f.Equals(id + ".ps1", StringComparison.OrdinalIgnoreCase)) is { } manifest)
        {
            return new(PackageContentKind.PowerShell, manifest);
        }

        if (packageTypes.FirstOrDefault(t => DotNetPackageTypes.Contains(t, StringComparer.OrdinalIgnoreCase)) is { } type)
        {
            return new(PackageContentKind.DotNet, "package type " + type);
        }

        if (paths.FirstOrDefault(f => DotNetFolders.Any(d => f.StartsWith(d, StringComparison.OrdinalIgnoreCase))) is { } assembly)
        {
            return new(PackageContentKind.DotNet, assembly);
        }

        return paths.Count == 0 ? new(PackageContentKind.MetadataOnly, "") : new(PackageContentKind.Other, paths[0]);
    }

    /// <summary>
    /// Null when a feed used for <paramref name="purpose"/> takes a package with this content, else why not. A PowerShell feed
    /// takes modules and scripts only; a Chocolatey or NuGet feed refuses the other two kinds it can recognise, and takes
    /// what it cannot tell apart - a portable Chocolatey package is tools and nothing else, and so is an old build-tool package.
    /// </summary>
    public string? RefusalFor(FeedPurpose purpose, string id, string feedName)
    {
        var refused = purpose switch
        {
            FeedPurpose.PowerShell => Kind != PackageContentKind.PowerShell,
            FeedPurpose.NuGet => Kind is PackageContentKind.PowerShell or PackageContentKind.Chocolatey,
            FeedPurpose.Chocolatey => Kind is PackageContentKind.PowerShell or PackageContentKind.DotNet,
            _ => false,
        };
        if (!refused)
        {
            return null;
        }

        var found = Kind switch
        {
            PackageContentKind.Chocolatey => $"a Chocolatey package ({Evidence})",
            PackageContentKind.PowerShell => $"a PowerShell module or script ({Evidence})",
            PackageContentKind.DotNet => $"a .NET package ({Evidence})",
            PackageContentKind.MetadataOnly => "a package with no files",
            _ => $"a package without {id}.psd1 or {id}.ps1 at its root",
        };
        var wanted = purpose switch
        {
            FeedPurpose.PowerShell => "PowerShell modules",
            FeedPurpose.NuGet => "NuGet packages",
            _ => "Chocolatey packages",
        };
        return $"{id} is {found}, and feed '{feedName}' is for {wanted}. Push it to another feed, or set this feed's \"Used for\" to any client.";
    }

    /// <summary>What the package format itself adds to every archive.</summary>
    private static bool IsPackagingFile(string path) =>
        path.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !path.Contains('/', StringComparison.Ordinal)
        || path.StartsWith("_rels/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("package/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase)
        || path.Equals(".signature.p7s", StringComparison.OrdinalIgnoreCase);
}
