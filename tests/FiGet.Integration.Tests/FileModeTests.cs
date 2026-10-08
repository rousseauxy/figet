using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using FiGet.Web.Configuration;
using FiGet.Web.Security;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>
/// What FiGet leaves on the volume, for the deployment where two clusters share one: each runs under its own user id
/// and the same group, so a file the group cannot write is a file the other cluster can never replace or delete.
///
/// Only meaningful on Linux, which is where it runs in CI and where every deployment runs.
/// </summary>
public sealed class FileModeTests(SqliteServerFixture server) : IClassFixture<SqliteServerFixture>
{
    private const UnixFileMode GroupReadWrite = UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

    [Fact]
    public async Task A_pushed_package_and_the_folders_holding_it_are_writable_by_the_group()
    {
        if (!OperatingSystem.IsLinux())
        {
            // Written as a branch rather than a skip helper so the platform analyser can see the guard.
            Assert.Skip("File modes are a Unix concept; this runs where the deployment runs.");
            return;
        }

        var id = FiGetServerFixture.UniqueId("Mode.Pushed");
        await PushAsync("public", id, "1.0.0");

        var root = server.Services.GetRequiredService<StoragePaths>().Root;
        var file = Directory.EnumerateFiles(Path.Combine(root, "files"), $"*{id.ToLowerInvariant()}*.nupkg", SearchOption.AllDirectories).FirstOrDefault();
        Assert.True(file is not null, "The pushed package was not found on the volume.");

        var mode = File.GetUnixFileMode(file!);
        Assert.True(mode.HasFlag(UnixFileMode.GroupWrite), $"{file} is {mode}: the other cluster could never replace it.");
        Assert.True(mode.HasFlag(UnixFileMode.GroupRead), $"{file} is {mode}: the other cluster could not even read it.");

        // A directory needs the execute bit too, or the group can list it and not create or remove entries in it.
        //
        // Only as far up as the folder FiGet makes for itself. The storage root above it is the deployment's: a volume
        // mount, whose mode the platform sets (on OpenShift through the namespace's fsGroup), and here a temporary
        // directory this test host made under its own umask. A umask cannot reach back and change what it was handed.
        var ours = Path.Combine(root, "files");
        for (var directory = Path.GetDirectoryName(file!); directory is not null && directory.StartsWith(ours, StringComparison.Ordinal); directory = Path.GetDirectoryName(directory))
        {
            var directoryMode = File.GetUnixFileMode(directory);
            Assert.True(
                directoryMode.HasFlag(UnixFileMode.GroupWrite) && directoryMode.HasFlag(UnixFileMode.GroupExecute),
                $"{directory} is {directoryMode}: the other cluster could not add or remove files in it.");
        }
    }

    /// <summary>
    /// The mask is the mechanism, so assert the mechanism as well as its effect: a failure here says "the umask did not
    /// take", where the test above only says "a file came out wrong".
    /// </summary>
    [Fact]
    public void The_process_runs_with_a_mask_that_allows_the_group()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("umask is a Unix concept.");
            return;
        }

        ProcessUmask.AllowTheGroup();

        Assert.NotNull(ProcessUmask.Changed);
        Assert.Equal(0b000_000_010u, ProcessUmask.Changed!.Value.Applied);
    }

    private async Task PushAsync(string feed, string id, string version)
    {
        using var client = server.CreateClient(FiGetServerFixture.AdminToken);
        using var package = TestPackages.Create(id, version);
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "package", "package.nupkg");
        HttpAssert.Status(HttpStatusCode.Created, await client.PutAsync($"nuget/{feed}/v3/publish", content));
    }
}
