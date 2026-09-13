namespace FiGet.Application.Ports;

/// <summary>A file being downloaded from somewhere else, to be stored in an asset directory.</summary>
public sealed class RemoteFile(Stream content, string? contentType, string? fileName, IDisposable owner) : IAsyncDisposable
{
    public Stream Content { get; } = content;

    /// <summary>The content type the remote server sent, if any.</summary>
    public string? ContentType { get; } = contentType;

    /// <summary>The last segment of the URL the file was finally served from, after redirects.</summary>
    public string? FileName { get; } = fileName;

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
        owner.Dispose();
    }
}

/// <summary>The fetch was refused or failed. The message is written for the person who asked.</summary>
public sealed class RemoteFetchException(string message, bool refused, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>True when the server declined to fetch (a forbidden address); false when the remote failed.</summary>
    public bool Refused { get; } = refused;
}

/// <summary>Downloads a file by URL, for pinning a vendor installer into an asset directory.</summary>
public interface IRemoteFileSource
{
    /// <summary>Opens the file. Throws <see cref="RemoteFetchException"/> when it cannot or may not be fetched.</summary>
    Task<RemoteFile> OpenAsync(Uri url, CancellationToken cancellationToken);
}
