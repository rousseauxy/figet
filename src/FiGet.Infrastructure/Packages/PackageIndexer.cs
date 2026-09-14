using System.Security.Cryptography;
using FiGet.Application.Ports;
using FiGet.Domain.Packages;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace FiGet.Infrastructure.Packages;


public sealed class PackageIndexer : IPackageIndexer
{
    /// <summary>nuget.org's limit on package id length.</summary>
    public const int MaxIdLength = 100;

    /// <summary>Longest normalised version accepted; keeps storage paths and index keys bounded.</summary>
    public const int MaxVersionLength = 64;

    /// <summary>Largest nuspec read. A real one is kilobytes; the largest seen on a gallery is under a megabyte of tags.</summary>
    public const int MaxNuspecBytes = 4 * 1024 * 1024;

    public async Task<IndexedPackage> IndexAsync(Stream nupkg, CancellationToken cancellationToken)
    {
        if (!nupkg.CanSeek)
        {
            throw new ArgumentException("The package stream must be seekable.", nameof(nupkg));
        }

        nupkg.Position = 0;
        var sha512 = Convert.ToBase64String(await SHA512.HashDataAsync(nupkg, cancellationToken));
        var size = nupkg.Length;
        nupkg.Position = 0;

        try
        {
            using var reader = new PackageArchiveReader(nupkg, leaveStreamOpen: true);
            var nuspecBytes = await ReadNuspecAsync(reader, cancellationToken);
            var nuspec = new NuspecReader(new MemoryStream(nuspecBytes, writable: false));

            var id = nuspec.GetId();
            if (string.IsNullOrWhiteSpace(id) || id.Length > MaxIdLength || !PackageIdValidator.IsValidPackageId(id))
            {
                throw new InvalidPackageException($"The package id '{id}' is not valid.");
            }

            var version = nuspec.GetVersion();
            var normalized = version.ToNormalizedString();
            if (normalized.Length > MaxVersionLength)
            {
                throw new InvalidPackageException($"The version '{normalized}' is longer than {MaxVersionLength} characters.");
            }

            var groups = nuspec.GetDependencyGroups().ToList();
            var license = nuspec.GetLicenseMetadata();
            var repository = nuspec.GetRepositoryMetadata();
            var packageTypes = nuspec.GetPackageTypes().Select(t => t.Name).ToList();

            return new IndexedPackage
            {
                Id = id,
                Version = version,
                OriginalVersion = version.OriginalVersion ?? normalized,
                IsSemVer2 = version.IsSemVer2 || groups.Any(g => g.Packages.Any(p => IsSemVer2(p.VersionRange))),
                Authors = nuspec.GetAuthors() ?? "",
                Description = nuspec.GetDescription() ?? "",
                Summary = nuspec.GetSummary() ?? "",
                Title = nuspec.GetTitle() ?? "",
                Tags = (nuspec.GetTags() ?? "").Trim(),
                IconUrl = nuspec.GetIconUrl() ?? "",
                LicenseUrl = nuspec.GetLicenseUrl() ?? "",
                LicenseExpression = license?.Type == LicenseType.Expression ? license.License : "",
                ProjectUrl = nuspec.GetProjectUrl() ?? "",
                RepositoryUrl = repository?.Url ?? "",
                RepositoryType = repository?.Type ?? "",
                ReleaseNotes = nuspec.GetReleaseNotes() ?? "",
                Copyright = nuspec.GetCopyright() ?? "",
                Language = nuspec.GetLanguage() ?? "",
                MinClientVersion = nuspec.GetMinClientVersion()?.ToString() ?? "",
                RequireLicenseAcceptance = nuspec.GetRequireLicenseAcceptance(),
                PackageTypes = packageTypes.Count == 0 ? ["Dependency"] : packageTypes,
                DependencyGroups = groups
                    .Select(g => new IndexedDependencyGroup(
                        FrameworkFolder(g.TargetFramework),
                        g.Packages.Select(p => new IndexedDependency(p.Id, RangeString(p.VersionRange))).ToList()))
                    .ToList(),
                Size = size,
                Sha512 = sha512,
                Nuspec = nuspecBytes,
            };
        }
        catch (InvalidPackageException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or PackagingException or FormatException or ArgumentException or System.Xml.XmlException)
        {
            throw new InvalidPackageException($"The file is not a valid package: {ex.Message}", ex);
        }
        finally
        {
            nupkg.Position = 0;
        }
    }

    private static async Task<byte[]> ReadNuspecAsync(PackageArchiveReader reader, CancellationToken cancellationToken)
    {
        string nuspecPath;
        try
        {
            nuspecPath = reader.GetNuspecFile();
        }
        catch (PackagingException ex)
        {
            throw new InvalidPackageException("The package does not contain a nuspec file.", ex);
        }

        // Read up to the cap, never whole: a zip entry declares any length it likes and deflate expands a thousandfold,
        // so a package under the upload limit could still carry a nuspec of gigabytes, and this used to hold it all.
        await using var stream = reader.GetStream(nuspecPath);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxNuspecBytes)
            {
                throw new InvalidPackageException($"The nuspec is larger than {MaxNuspecBytes / (1024 * 1024)} MB.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string FrameworkFolder(NuGetFramework framework) =>
        framework.IsAny || framework.IsUnsupported ? "" : framework.GetShortFolderName();

    private static string RangeString(VersionRange? range) =>
        range is null || range.Equals(VersionRange.All) ? "" : range.ToNormalizedString();

    private static bool IsSemVer2(VersionRange? range) =>
        range is not null
        && ((range.HasLowerBound && range.MinVersion!.IsSemVer2) || (range.HasUpperBound && range.MaxVersion!.IsSemVer2));
}
