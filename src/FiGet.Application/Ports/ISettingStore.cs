namespace FiGet.Application.Ports;

/// <summary>
/// Settings an administrator changes at runtime. Deliberately a key-value store rather than a typed
/// row per setting: these are few, they are read one at a time, and each one that arrives should not
/// need a migration of its own.
/// </summary>
public interface ISettingStore
{
    /// <summary>The stored value, or null when nothing has been stored - which means "use the default".</summary>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Writes the value, creating the row when it is the first time.</summary>
    Task SetAsync(string key, string value, string? updatedBy, CancellationToken cancellationToken);
}
