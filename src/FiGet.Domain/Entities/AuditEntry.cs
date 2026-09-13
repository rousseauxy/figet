namespace FiGet.Domain.Entities;

/// <summary>
/// One change, kept in the database so "what happened last Tuesday" has an answer after the container's log has rotated.
/// Written as the console line is, from the same call; pruned after a configured number of days.
/// </summary>
public sealed class AuditEntry
{
    public long Key { get; set; }

    public DateTime WhenUtc { get; set; }

    /// <summary>A dotted verb: <c>package.push</c>, <c>signin.refused</c>, <c>access.set</c>.</summary>
    public required string Action { get; set; }

    /// <summary>What it acted on: a package id, a feed name, an account, a token name.</summary>
    public required string Subject { get; set; }

    /// <summary><c>user:name</c>, <c>token:name</c>, <c>anonymous</c> or <c>system</c>, as the request log says it.</summary>
    public required string Actor { get; set; }

    /// <summary>Lower-cased, for filtering without depending on a collation.</summary>
    public required string ActorLower { get; set; }

    /// <summary>The feed or asset directory involved, when there is one; the filter on the audit page.</summary>
    public string? Feed { get; set; }

    public string? FeedLower { get; set; }

    public string Detail { get; set; } = "";

    /// <summary>The address the request came from, as a trusted proxy forwarded it.</summary>
    public string Caller { get; set; } = "";
}
