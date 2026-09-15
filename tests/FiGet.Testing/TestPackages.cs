using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace FiGet.Testing;

/// <summary>Builds real .nupkg and .snupkg files in memory with NuGet's own package builder.</summary>
public static class TestPackages
{
    /// <summary>A minimal valid package. <paramref name="configure"/> can add metadata or dependencies.</summary>
    public static MemoryStream Create(string id, string version, Action<PackageBuilder>? configure = null)
    {
        var builder = new PackageBuilder
        {
            Id = id,
            Version = NuGetVersion.Parse(version),
            Description = $"Test package {id}",
        };
        builder.Authors.Add("FiGet Tests");
        builder.Files.Add(new InMemoryFile("lib/netstandard2.0/_._", [0]));
        configure?.Invoke(builder);
        return Save(builder);
    }

    /// <summary>A symbol package carrying the given PDB bytes as <c>lib/netstandard2.0/{pdbName}</c>.</summary>
    public static MemoryStream CreateSymbols(string id, string version, byte[] pdb, string pdbName)
    {
        var builder = new PackageBuilder
        {
            Id = id,
            Version = NuGetVersion.Parse(version),
            Description = $"Symbols for {id}",
        };
        builder.Authors.Add("FiGet Tests");
        builder.PackageTypes.Add(new PackageType("SymbolsPackage", PackageType.EmptyVersion));
        builder.Files.Add(new InMemoryFile($"lib/netstandard2.0/{pdbName}", pdb));
        return Save(builder);
    }

    /// <summary>A symbol package carrying several PDB files, each at the path given.</summary>
    public static MemoryStream CreateSymbols(string id, string version, IReadOnlyList<(string Path, byte[] Content)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var builder = new PackageBuilder
        {
            Id = id,
            Version = NuGetVersion.Parse(version),
            Description = $"Symbols for {id}",
        };
        builder.Authors.Add("FiGet Tests");
        builder.PackageTypes.Add(new PackageType("SymbolsPackage", PackageType.EmptyVersion));
        foreach (var (path, content) in files)
        {
            builder.Files.Add(new InMemoryFile(path, content));
        }

        return Save(builder);
    }

    /// <summary>Adds a file with the given bytes to the package.</summary>
    public static void AddContent(this PackageBuilder builder, string path, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Files.Add(new InMemoryFile(path, content));
    }

    public static void AddDependency(this PackageBuilder builder, string framework, string id, string range)
    {
        var targetFramework = NuGetFramework.Parse(framework);
        var existing = builder.DependencyGroups.FirstOrDefault(g => g.TargetFramework.Equals(targetFramework));
        var dependencies = existing?.Packages.ToList() ?? [];
        dependencies.Add(new NuGet.Packaging.Core.PackageDependency(id, VersionRange.Parse(range)));
        if (existing is not null)
        {
            builder.DependencyGroups.Remove(existing);
        }

        builder.DependencyGroups.Add(new PackageDependencyGroup(targetFramework, dependencies));
    }

    /// <summary>The portable PDB of this test assembly, which the SDK builds by default.</summary>
    public static byte[] PortablePdb() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FiGet.Testing.pdb"));

    private static MemoryStream Save(PackageBuilder builder)
    {
        var stream = new MemoryStream();
        builder.Save(stream);
        stream.Position = 0;
        return stream;
    }

    private sealed class InMemoryFile(string path, byte[] content) : IPackageFile
    {
        public string Path { get; } = path.Replace('/', System.IO.Path.DirectorySeparatorChar);

        public string EffectivePath => Path;

        public System.Runtime.Versioning.FrameworkName TargetFramework => null!;

        public NuGetFramework NuGetFramework => NuGetFramework.AnyFramework;

        public DateTimeOffset LastWriteTime { get; } = DateTimeOffset.UtcNow;

        public Stream GetStream() => new MemoryStream(content, writable: false);
    }
}
