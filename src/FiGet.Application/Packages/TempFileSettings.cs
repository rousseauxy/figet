namespace FiGet.Application.Packages;

/// <summary>
/// Where bytes are spooled while they are checked: <c>FiGet:Storage:TempPath</c>, or the system temp directory. On a pod
/// the system one is the container's small writable layer, which is why the path is configurable at all. Bound by the
/// host, like <c>ConnectorSettings</c>.
/// </summary>
public sealed class TempFileSettings
{
    public string? Root { get; set; }

    /// <summary>A temporary file that deletes itself when it is disposed, open for reading and writing.</summary>
    public FileStream Create(string prefix)
    {
        var directory = string.IsNullOrWhiteSpace(Root) ? Path.GetTempPath() : Root;
        Directory.CreateDirectory(directory);
        return new FileStream(
            Path.Combine(directory, prefix + Guid.NewGuid().ToString("N") + ".tmp"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    }
}
