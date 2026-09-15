using System.Text.RegularExpressions;

namespace FiGet.Domain.Feeds;

/// <summary>One line of rendered instructions: a command to copy, or a caption above the commands that follow.</summary>
public sealed record InstructionLine(string Text, bool IsCaption);

/// <summary>
/// The commands a feed's pages show - how to connect to it, how to install a package, how to download a file - as
/// templates a feed manager can reword. One command per line; a line starting with <c>#</c> is a caption; placeholders in
/// braces are filled in from the page. An empty template means the default below.
/// </summary>
public static partial class InstructionTemplates
{
    /// <summary>On a package and a version page. Placeholders: <c>{id} {version} {feed} {feedUrl} {v3Url}</c>.</summary>
    public const string DefaultPackage =
        """
        Install-Module -Name {id} -RequiredVersion {version} -Repository {feed}
        Install-PSResource -Name {id} -Version {version} -Repository {feed}
        dotnet add package {id} --version {version} --source {v3Url}
        """;

    /// <summary>On the feed's page, to connect a client once. Placeholders: <c>{feed} {feedUrl} {v3Url}</c>.</summary>
    public const string DefaultFeed =
        """
        # Windows PowerShell 5.1 (PowerShellGet)
        Register-PSRepository -Name {feed} -SourceLocation {feedUrl}/ -PublishLocation {feedUrl}/ -InstallationPolicy Trusted
        # PowerShell 7 (PSResourceGet): the v2 address, where exact versions, wildcards and tags work
        Register-PSResourceRepository -Name {feed} -Uri {feedUrl}/api/v2 -Trusted
        # .NET
        dotnet nuget add source {v3Url} --name {feed}
        """;

    /// <summary>On an asset directory's page, for the folder shown. Placeholders: <c>{directory} {folderUrl}</c>; <c>&lt;file&gt;</c> is left for the reader.</summary>
    public const string DefaultFiles =
        """
        Invoke-WebRequest -Uri {folderUrl}<file> -OutFile <file>
        curl -fLO {folderUrl}<file>
        """;

    public const int MaxLength = 4000;

    /// <summary>
    /// Fills in the placeholders. An unknown placeholder is left as written, so a typo shows on the page instead of
    /// disappearing; blank lines are dropped.
    /// </summary>
    public static IReadOnlyList<InstructionLine> Render(string? template, string defaultTemplate, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var source = string.IsNullOrWhiteSpace(template) ? defaultTemplate : template;
        var lines = new List<InstructionLine>();
        foreach (var raw in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var caption = line.StartsWith('#');
            var text = Placeholder().Replace(caption ? line[1..].Trim() : line, m => values.TryGetValue(m.Groups["name"].Value, out var value) ? value : m.Value);
            lines.Add(new InstructionLine(text, caption));
        }

        return lines;
    }

    /// <summary>What to store for a template typed into the settings page: null when it is the default, so a later change to the default reaches the feed.</summary>
    public static string? Normalize(string? typed, string defaultTemplate)
    {
        var value = (typed ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return value.Length == 0 || value == defaultTemplate.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() ? null : value;
    }

    [GeneratedRegex(@"\{(?<name>[a-zA-Z0-9]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
