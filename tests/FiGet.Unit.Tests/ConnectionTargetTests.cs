using FiGet.Infrastructure.Persistence;

namespace FiGet.Unit.Tests;

/// <summary>
/// What the database page is allowed to say about where the data lives. The connection string is the one place in this
/// server where a password sits beside an address, so the rule is an allow-list and this is the test that keeps it one.
/// </summary>
public sealed class ConnectionTargetTests
{
    /// <summary>
    /// Everything that is not the host or the catalogue is dropped, including a key nobody has thought of yet - which is
    /// the whole reason for an allow-list. The made-up key stands in for the next one a provider adds.
    /// </summary>
    [Fact]
    public void A_sql_server_target_is_the_host_and_the_catalogue_and_nothing_else()
    {
        const string ConnectionString =
            "Data Source=sql01.example.org,1433;Initial Catalog=FiGet;User ID=figet_app;Password=hunter2-SECRET;" +
            "Application Intent=ReadWrite;Some Future Key=another-SECRET;Encrypt=False";

        var target = ConnectionTarget.SqlServerHostAndCatalog(ConnectionString);

        Assert.Equal("sql01.example.org,1433 / FiGet", target);
        Assert.DoesNotContain("SECRET", target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("figet_app", target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Encrypt", target, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The same string written with the other spellings a provider accepts, since a deny-list would need both.</summary>
    [Theory]
    [InlineData("Server=sql01;Database=FiGet;Pwd=SECRET", "sql01 / FiGet")]
    [InlineData("Address=10.0.0.4;Initial Catalog=FiGet;password=SECRET", "10.0.0.4 / FiGet")]
    [InlineData("Data Source=sql01", "sql01")]
    [InlineData("Initial Catalog=FiGet", "FiGet")]
    public void A_target_is_read_by_any_spelling_of_the_two_keys(string connectionString, string expected)
    {
        var target = ConnectionTarget.SqlServerHostAndCatalog(connectionString);

        Assert.Equal(expected, target);
        Assert.DoesNotContain("SECRET", target, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A string that says nothing about the server is answered as such, never by showing the string.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Password=SECRET;Integrated Security=true")]
    public void A_target_without_a_host_or_catalogue_is_unknown(string connectionString)
    {
        var target = ConnectionTarget.SqlServerHostAndCatalog(connectionString);

        Assert.Equal("unknown", target);
        Assert.DoesNotContain("SECRET", target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_sqlite_target_is_the_file_it_names()
    {
        var target = ConnectionTarget.SqliteFile("Data Source=/data/figet.db;Password=SECRET;Cache=Shared");

        Assert.EndsWith("figet.db", target, StringComparison.Ordinal);
        Assert.True(Path.IsPathRooted(target), "The path is shown in full, so two instances are told apart.");
        Assert.DoesNotContain("SECRET", target, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A database that is not a file has no path to show, and the page says so instead of inventing one.</summary>
    [Fact]
    public void A_sqlite_database_in_memory_names_no_file()
    {
        Assert.Equal("", ConnectionTarget.SqliteFile("Data Source=:memory:"));
    }
}
