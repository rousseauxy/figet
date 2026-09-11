using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FiGet.Persistence.SqlServer;

public static class SqlServerProvider
{
    public static readonly string MigrationsAssembly = typeof(SqlServerProvider).Assembly.GetName().Name!;

    public static DbContextOptionsBuilder UseFiGetSqlServer(this DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlServer(connectionString, sql => sql.MigrationsAssembly(MigrationsAssembly));
}

/// <summary>Used by <c>dotnet ef migrations add --project src/FiGet.Persistence.SqlServer</c>. Never connects.</summary>
public sealed class SqlServerDesignTimeFactory : IDesignTimeDbContextFactory<FiGetDbContext>
{
    public FiGetDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<FiGetDbContext>();
        builder.UseFiGetSqlServer(@"Server=(localdb)\MSSQLLocalDB;Database=figet_design;Trusted_Connection=True;TrustServerCertificate=True");
        return new FiGetDbContext(builder.Options);
    }
}
