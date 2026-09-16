using FiGet.Web.Configuration;

namespace FiGet.Integration.Tests;

/// <summary>
/// What this build calls itself. The rule matters twice over since the image stamps a version: the Dockerfile's default
/// is <c>1.0.0</c> precisely because that is the value this reads as "nobody said".
///
/// It needs no server: it sits here only because the unit tests deliberately cannot see FiGet.Web.
/// </summary>
public sealed class ServerVersionTests
{
    [Fact]
    public void A_configured_version_wins()
    {
        Assert.Equal("1.2.0", ServerVersion.Resolve("1.2.0"));
    }

    /// <summary>
    /// A configured value is somebody's decision, so it is shown as given - 1.0.0 included. The rule about 1.0.0 is
    /// about the *assembly's* stamp, which is what the SDK writes when nothing named a version, and which is the path
    /// the image's default build argument deliberately takes.
    /// </summary>
    [Fact]
    public void A_configured_version_is_taken_at_face_value()
    {
        Assert.Equal("1.0.0", ServerVersion.Resolve("1.0.0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_unstamped_assembly_says_so_rather_than_claiming_a_release(string? configured)
    {
        // Nothing stamps a version into a plain `dotnet build`, so this exercises exactly what an image built without
        // the VERSION argument would report.
        Assert.Equal(ServerVersion.None, ServerVersion.Resolve(configured));
    }
}
