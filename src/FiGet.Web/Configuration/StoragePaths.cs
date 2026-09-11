using Microsoft.Extensions.Options;

namespace FiGet.Web.Configuration;

/// <summary>Resolves the storage root and the effective connection string from configuration.</summary>
public sealed class StoragePaths
{
    public StoragePaths(IOptions<FiGetOptions> options, IWebHostEnvironment environment)
    {
        var value = options.Value;
        var root = string.IsNullOrWhiteSpace(value.Storage.Root) ? "data" : value.Storage.Root;
        Root = Path.GetFullPath(Path.IsPathRooted(root) ? root : Path.Combine(environment.ContentRootPath, root));

        if (!string.IsNullOrWhiteSpace(value.Database.ConnectionString))
        {
            ConnectionString = value.Database.ConnectionString;
        }
        else if (value.Database.Provider == DatabaseProvider.Sqlite)
        {
            ConnectionString = $"Data Source={Path.Combine(Root, "figet.db")}";
        }
        else
        {
            throw new InvalidOperationException("FiGet:Database:ConnectionString is required when the provider is SqlServer.");
        }
    }

    /// <summary>Absolute storage root.</summary>
    public string Root { get; }

    public string ConnectionString { get; }
}
