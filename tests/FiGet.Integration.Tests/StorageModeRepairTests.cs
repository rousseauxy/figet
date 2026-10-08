using FiGet.Application.Ports;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Web.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>
/// Repairing the modes of files written before this server set its umask, which is what an upgrade leaves behind on a
/// volume two clusters share. Linux only, which is where every deployment runs and where CI runs.
/// </summary>
public sealed class StorageModeRepairTests(SqliteServerFixture server) : IClassFixture<SqliteServerFixture>
{
    [Fact]
    public async Task A_file_the_group_cannot_write_is_repaired_and_the_rest_left_alone()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("File modes are a Unix concept; this runs where the deployment runs.");
            return;
        }

        var root = server.Services.GetRequiredService<StoragePaths>().Root;
        var feeds = Path.Combine(root, "files", "feeds", "9001", "packages", "repair.probe", "1.0.0");
        Directory.CreateDirectory(feeds);

        // As an older version left it: readable by the group, writable only by its owner.
        var stale = Path.Combine(feeds, "repair.probe.1.0.0.nupkg");
        await File.WriteAllTextAsync(stale, "a package written before the umask was set", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(stale, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.SetUnixFileMode(feeds, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        // Something already correct, to show the count is of what actually changed.
        var fine = Path.Combine(feeds, "repair.probe.1.0.0.nuspec");
        await File.WriteAllTextAsync(fine, "<package />", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(fine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);

        StorageModeReport report;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            report = await scope.ServiceProvider.GetRequiredService<IStorageAudit>().RepairModesAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(report.Supported);
        Assert.True(report.Changed >= 2, $"The stale file and its folder should both have changed; {report.Changed} did.");

        var repaired = File.GetUnixFileMode(stale);
        Assert.True(repaired.HasFlag(UnixFileMode.GroupWrite), $"{repaired}: another instance still could not replace it.");
        Assert.True(repaired.HasFlag(UnixFileMode.GroupRead));

        // The folder needs execute as well, or the group can list it and not write into it.
        var folder = File.GetUnixFileMode(feeds);
        Assert.True(folder.HasFlag(UnixFileMode.GroupWrite) && folder.HasFlag(UnixFileMode.GroupExecute), $"{folder}: entries could not be added or removed.");

        // Nothing beyond the group's bits: the owner's rights are untouched and others gain nothing.
        Assert.True(repaired.HasFlag(UnixFileMode.UserRead) && repaired.HasFlag(UnixFileMode.UserWrite));
        Assert.False(repaired.HasFlag(UnixFileMode.OtherWrite), $"{repaired}: repairing must not open a file to everyone.");

        Directory.Delete(Path.Combine(root, "files", "feeds", "9001"), recursive: true);
    }

    /// <summary>
    /// Running it twice must report nothing the second time: that is what tells an operator the volume is now
    /// consistent, rather than leaving them unsure whether it did anything.
    /// </summary>
    [Fact]
    public async Task A_second_run_has_nothing_left_to_do()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("File modes are a Unix concept.");
            return;
        }

        await using var scope = server.Services.CreateAsyncScope();
        var audit = scope.ServiceProvider.GetRequiredService<IStorageAudit>();

        await audit.RepairModesAsync(TestContext.Current.CancellationToken);
        var second = await audit.RepairModesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, second.Changed);
        Assert.Equal(0, second.Refused);
    }
}
