using FiGet.Domain.Packages;

namespace FiGet.Application.Ports;

/// <summary>
/// Reads a .nupkg and returns what the server needs to know about it. A port because reading the archive
/// means a zip reader and a nuspec parser, which are the implementation's business and not a workflow's.
/// </summary>
public interface IPackageIndexer
{
    /// <summary>Reads and validates a nupkg. The stream must be seekable; its position is restored to 0.</summary>
    /// <exception cref="InvalidPackageException">The file is not a valid package.</exception>
    Task<IndexedPackage> IndexAsync(Stream nupkg, CancellationToken cancellationToken);
}
