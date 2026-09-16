using System.Reflection;

namespace FiGet.Web.Configuration;

/// <summary>What is running, in the words a person reads.</summary>
public static class ServerVersion
{
    /// <summary>The SDK stamps this when a project names no version of its own.</summary>
    private const string Unstamped = "1.0.0";

    /// <summary>Shown when nothing has said what this is. Saying so beats inventing a release number.</summary>
    public const string None = "version not set";

    /// <summary>
    /// A configured value wins, so a deployment can stamp the image tag it built; otherwise the assembly's
    /// informational version, with the build metadata after "+" dropped because it is a commit hash and not what
    /// anyone reading a menu wants.
    ///
    /// <c>1.0.0</c> counts as nothing said. The SDK writes it whenever a project names no version, so taking it at face
    /// value would have every unversioned build - every local run, and this server until somebody stamps one - announce
    /// itself as a first release.
    /// </summary>
    public static string Resolve(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var assembly = typeof(FiGetApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var stamped = string.IsNullOrWhiteSpace(assembly) ? "" : assembly.Split('+')[0];
        return stamped.Length == 0 || stamped == Unstamped ? None : stamped;
    }
}
