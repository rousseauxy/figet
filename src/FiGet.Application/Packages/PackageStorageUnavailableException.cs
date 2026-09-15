namespace FiGet.Application.Packages;

/// <summary>
/// A package could not be written to storage: a full disk, a permission, a share that went away. Nothing was stored, and
/// the protocols answer 503 with a retry hint rather than a bare 500, because the request itself was fine and trying again
/// later is exactly what should happen.
/// </summary>
public sealed class PackageStorageUnavailableException : Exception
{
    public PackageStorageUnavailableException()
    {
    }

    public PackageStorageUnavailableException(string message)
        : base(message)
    {
    }

    public PackageStorageUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
