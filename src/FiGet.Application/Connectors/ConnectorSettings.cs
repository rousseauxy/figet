namespace FiGet.Application.Connectors;

/// <summary>
/// How the connector talks to upstreams. Bound from configuration by the host.
///
/// It lives beside the connector rather than beside the client that reads it: both the workflow and the
/// adapter need these numbers, and the workflow is not allowed to see the adapter.
/// </summary>
public sealed class ConnectorSettings
{
    /// <summary>How long a cached upstream version list stays usable before it is fetched again.</summary>
    public TimeSpan UpstreamIndexTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long one upstream call may take before that upstream counts as unavailable. Listing a package
    /// with hundreds of versions on a v2 gallery is a paged walk of several megabytes, so this is not the
    /// latency of a single request.
    /// </summary>
    public TimeSpan UpstreamTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Where a package downloaded from an upstream is buffered while it is indexed: <c>FiGet:Storage:TempPath</c>, or the
    /// system temp directory. On a pod the system one is the container's small writable layer, and an installer-sized
    /// package filled it before it reached storage.
    /// </summary>
    public string? TempPath { get; set; }
}
