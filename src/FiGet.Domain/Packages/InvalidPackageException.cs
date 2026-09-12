namespace FiGet.Domain.Packages;

/// <summary>A pushed file is not an acceptable package. The message is safe to return to the client.</summary>
public sealed class InvalidPackageException : Exception
{
    public InvalidPackageException()
    {
    }

    public InvalidPackageException(string message)
        : base(message)
    {
    }

    public InvalidPackageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
