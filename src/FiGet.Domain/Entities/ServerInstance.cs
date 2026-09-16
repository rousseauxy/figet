namespace FiGet.Domain.Entities;

/// <summary>
/// One running copy of this server, as it reports itself. Written by every replica and read by nobody else, so that a
/// deployment of several can be seen as a whole: how many are up, what each is running, and which one stopped saying so.
///
/// It is a report and never a decision. Which replica runs a job is <see cref="JobLease"/>'s business, and no code reads
/// this to choose anything - otherwise a stale row would become an outage rather than a stale row.
/// </summary>
public sealed class ServerInstance
{
    /// <summary>The instance's own id, and the primary key: machine name and a value of this process's start.</summary>
    public required string Id { get; set; }

    /// <summary>The machine, which on a cluster is the pod: several rows may share it over a deployment's life.</summary>
    public required string Machine { get; set; }

    /// <summary>What this one is running, so a half-finished rollout shows as two versions rather than as nothing.</summary>
    public required string Version { get; set; }

    public DateTime StartedUtc { get; set; }

    /// <summary>Last time it said it was there. An instance that stopped keeps its row until it is swept.</summary>
    public DateTime LastSeenUtc { get; set; }
}
