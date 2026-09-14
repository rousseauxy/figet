namespace FiGet.Domain.Entities;

/// <summary>
/// Which instance runs a periodic job, until when. With several replicas each would otherwise run retention, the audit
/// prune and the upload sweep on its own clock: harmless, since a second run finds nothing, but every run is a scan of
/// the database or the storage volume, and retention's audit entries came in once per replica.
/// </summary>
public sealed class JobLease
{
    /// <summary>The job, and the primary key: one row per job.</summary>
    public required string Name { get; set; }

    /// <summary>The instance holding it: its machine name - a pod's name - and a value of its own start.</summary>
    public required string Holder { get; set; }

    public DateTime ExpiresUtc { get; set; }
}

/// <summary>The jobs that take a lease, named once.</summary>
public static class JobLeaseNames
{
    public const string Retention = "retention";

    public const string AuditPrune = "audit-prune";

    public const string UploadSweep = "upload-sweep";
}
