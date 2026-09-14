using System.Reflection;
using System.Text.RegularExpressions;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;

namespace FiGet.Unit.Tests;

/// <summary>
/// The architectural guard for the layer split.
///
/// A layer split is only worth the churn if something stops it drifting back. The build plan has said
/// "no ASP.NET, no EF references" since before the first line was written, and the NuGet.Protocol client
/// still ended up in the same assembly as the entities — because nothing failed when it did. The
/// compiler is perfectly happy to let an EF attribute or an HttpContext lookup into a rule about
/// versions.
///
/// So this asserts on the compiled assembly's reference list, which is the only thing that cannot be
/// argued with, and on the project files, which catch a reference that is declared but not yet used.
/// If one of these fails, the question is not "how do I make the test pass" but "what leaked in, and
/// which layer should it have gone to".
/// </summary>
public sealed class LayerBoundaryTests
{
    private static readonly Assembly Domain = typeof(Feed).Assembly;
    private static readonly Assembly Application = typeof(IPackageStore).Assembly;

    private static string[] ReferencesOf(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(a => a.Name ?? "")];

    // ── the Domain layer ────────────────────────────────────────────────────────

    [Theory]
    // The web stack. A rule about versions must not be able to read a request.
    [InlineData("Microsoft.AspNetCore")]
    // Persistence. [Column] and [Precision] are the easy accidents — express them in the DbContext.
    [InlineData("Microsoft.EntityFrameworkCore")]
    // Logging, DI, configuration: all of it belongs to a layer that has something to configure.
    [InlineData("Microsoft.Extensions.")]
    // NuGet's client library talks to other servers over HTTP; NuGet.Packaging opens files on disk.
    // Both are adapters wearing a NuGet label, and the label is what makes them easy to wave through.
    [InlineData("NuGet.Protocol")]
    [InlineData("NuGet.Packaging")]
    public void Domain_does_not_reference(string forbiddenPrefix)
    {
        var leaked = ReferencesOf(Domain)
            .Where(r => r.StartsWith(forbiddenPrefix, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            leaked.Length == 0,
            $"FiGet.Domain must not depend on {forbiddenPrefix}*, but references: {string.Join(", ", leaked)}");
    }

    /// <summary>
    /// NuGet.Versioning is the one package the domain is allowed, and it is allowed on purpose: which
    /// version counts as latest is the rule this whole server exists to get right, and answering it with
    /// anything other than NuGet's own comparer would mean answering clients differently from every
    /// other NuGet server. See docs/decisions/0001.
    /// </summary>
    [Fact]
    public void Domain_references_NuGetVersioning_and_no_other_package()
    {
        var packages = PackageReferencesOf("FiGet.Domain");
        Assert.Equal(["NuGet.Versioning"], packages);
    }

    /// <summary>
    /// Domain sits at the bottom: it may not know about Application, Infrastructure or the host. Stated
    /// separately from the framework rule because the failure reads differently — a reference here means
    /// a cycle was about to be created, not that a library leaked in.
    /// </summary>
    [Fact]
    public void Domain_references_no_other_FiGet_project()
    {
        var leaked = ReferencesOf(Domain)
            .Where(r => r.StartsWith("FiGet.", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            leaked.Length == 0,
            "FiGet.Domain is the bottom layer and must reference no other FiGet project, but references: "
            + string.Join(", ", leaked));
    }

    // ── the Application layer ───────────────────────────────────────────────────

    /// <summary>
    /// Application states what the server does; it must not know how. A persistence or vendor reference
    /// here means a workflow has absorbed an implementation detail that belonged behind a port — which is
    /// precisely the failure this layer exists to prevent.
    ///
    /// Microsoft.Extensions.Logging.Abstractions is deliberately not forbidden: it is a contract, not an
    /// implementation, and a workflow will want a logger.
    /// </summary>
    [Theory]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("NuGet.Protocol")]
    [InlineData("NuGet.Packaging")]
    public void Application_does_not_reference(string forbiddenPrefix)
    {
        var leaked = ReferencesOf(Application)
            .Where(r => r.StartsWith(forbiddenPrefix, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            leaked.Length == 0,
            $"FiGet.Application must not depend on {forbiddenPrefix}*, but references: {string.Join(", ", leaked)}");
    }

    /// <summary>
    /// Application sees Domain and nothing else of ours. In particular it must never reference
    /// Infrastructure: that project holds the EF context and the adapters, so a reference in that
    /// direction would invert the dependency the whole split is built on.
    /// </summary>
    [Fact]
    public void Application_project_references_only_Domain()
    {
        Assert.Equal(["FiGet.Domain"], ProjectReferencesOf("FiGet.Application"));
    }

    // ── the HTTP and protocol layers ────────────────────────────────────────────

    /// <summary>
    /// The protocol projects speak HTTP and reach data through Application's ports; Http holds what they share. None of them
    /// may reach Infrastructure, and none may pull in EF or NuGet's client and packaging libraries on its own. Read from the
    /// project files, because this test assembly does not reference these projects: a reference declared there is how a
    /// leak starts. Added after the 2026-09-14 review found these five held only by convention.
    /// </summary>
    [Theory]
    [InlineData("FiGet.Http", new[] { "FiGet.Domain", "FiGet.Application" })]
    [InlineData("FiGet.Protocol.V2", new[] { "FiGet.Domain", "FiGet.Application", "FiGet.Http" })]
    [InlineData("FiGet.Protocol.V3", new[] { "FiGet.Domain", "FiGet.Application", "FiGet.Http" })]
    [InlineData("FiGet.Protocol.Management", new[] { "FiGet.Domain", "FiGet.Application", "FiGet.Http" })]
    [InlineData("FiGet.Protocol.Assets", new[] { "FiGet.Domain", "FiGet.Application", "FiGet.Http" })]
    public void Http_and_protocol_projects_reference_only_the_layers_below(string project, string[] allowed)
    {
        Assert.Equal(allowed, ProjectReferencesOf(project));

        var packages = PackageReferencesOf(project)
            .Where(p => p.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                || p.StartsWith("NuGet.Protocol", StringComparison.Ordinal)
                || p.StartsWith("NuGet.Packaging", StringComparison.Ordinal))
            .ToArray();
        Assert.True(packages.Length == 0, $"{project} must not reference {string.Join(", ", packages)}.");
    }

    // ── reading the project files ───────────────────────────────────────────────

    private static string[] ProjectReferencesOf(string project) =>
        [.. Regex
            .Matches(ProjectFile(project), @"<ProjectReference Include=""([^""]+)""")
            // Separators are normalised first: csproj paths are Windows-style, and on Linux a backslash
            // is an ordinary character, so GetFileNameWithoutExtension would hand back the whole path.
            .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', '/')))];

    private static string[] PackageReferencesOf(string project) =>
        [.. Regex
            .Matches(ProjectFile(project), @"<PackageReference Include=""([^""]+)""")
            .Select(m => m.Groups[1].Value)];

    private static string ProjectFile(string project) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", project, project + ".csproj"));

    /// <summary>Walks up from the test binary until the solution file turns up.</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "figet.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not find figet.slnx above the test binary.");
        return directory!.FullName;
    }
}
