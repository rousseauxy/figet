namespace FiGet.Application.Connectors;

/// <summary>
/// A version this feed does not hold could not be fetched because its upstream did not answer, rather than because the
/// upstream does not have it. A client told 404 here reports a missing package (Install-Module: "No match was found")
/// when the cause is a gallery that is down or a firewall dropping the connection; this becomes 502, or 504 for a timeout.
/// </summary>
public sealed class UpstreamUnavailableException : Exception
{
    public UpstreamUnavailableException()
    {
    }

    public UpstreamUnavailableException(string message)
        : base(message)
    {
    }

    public UpstreamUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public UpstreamUnavailableException(string message, bool timedOut)
        : base(message) => TimedOut = timedOut;

    /// <summary>Every failure was a timeout: the upstream never answered in time (504 rather than 502).</summary>
    public bool TimedOut { get; }

    /// <summary>
    /// The upstream answered, with a file that fails its own published hash. Still 502, but without "try again later" or a
    /// Retry-After: the next attempt gets the same file.
    /// </summary>
    public bool Refused { get; init; }
}
