using System.Text.RegularExpressions;

namespace FiGet.Unit.Tests;

/// <summary>
/// Nothing may extract an archive with an API that applies the mode recorded inside it.
///
/// FiGet runs on a volume two clusters share under different user ids and one group, and sets its umask so that what it
/// writes stays writable by that group. <c>ZipFile.ExtractToDirectory</c> and <c>ZipFileExtensions.ExtractToFile</c>
/// bypass that entirely on Unix: they apply the entry's own mode, so a package built on a machine with a tight umask
/// would arrive as 0644 and the other cluster could never replace or delete it.
///
/// Today every extraction reads the entry's stream and writes it through FiGet's own writers, which the umask governs.
/// This keeps it that way, because the failure is invisible until two clusters disagree months later.
/// </summary>
public sealed partial class ArchiveExtractionTests
{
    [Fact]
    public void No_source_file_extracts_an_archive_with_the_modes_recorded_in_it()
    {
        var source = SourceRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            if (ModePreservingExtraction().IsMatch(File.ReadAllText(file)))
            {
                offenders.Add(Path.GetRelativePath(source, file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These extract with the archive's own file modes, which ignores the umask and breaks a volume shared by two "
            + "clusters. Read the entry's stream and write it through the storage layer instead: "
            + string.Join(", ", offenders));
    }

    /// <summary>Walks up from the test binary to the directory holding the solution, so it works from any runner.</summary>
    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "figet.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "The repository root was not found above the test binary.");
        return Path.Combine(directory!.FullName, "src");
    }

    [GeneratedRegex(@"\b(ExtractToDirectory|ExtractToFile|ExtractRelativeToDirectory)\s*\(")]
    private static partial Regex ModePreservingExtraction();
}
