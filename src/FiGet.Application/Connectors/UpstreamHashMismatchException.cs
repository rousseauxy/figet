namespace FiGet.Application.Connectors;

/// <summary>
/// An upstream served a package file that does not match the hash it publishes for that version. The file is refused,
/// and asking again does not help: the gallery serves the same file every time (seen on QA, 2026-11-26, for 2017
/// versions of SqlServer and PSScriptAnalyzer whose gallery hash does not match the gallery's own file; FG-67).
/// </summary>
public sealed class UpstreamHashMismatchException : Exception
{
    public UpstreamHashMismatchException()
    {
    }

    public UpstreamHashMismatchException(string message)
        : base(message)
    {
    }

    public UpstreamHashMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
