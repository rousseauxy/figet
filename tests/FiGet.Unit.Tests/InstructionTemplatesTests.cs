using FiGet.Domain.Feeds;

namespace FiGet.Unit.Tests;

public sealed class InstructionTemplatesTests
{
    private static readonly Dictionary<string, string> Values = new() { ["id"] = "Pester", ["version"] = "5.7.1", ["feed"] = "modules" };

    [Fact]
    public void Placeholders_are_filled_captions_marked_and_blank_lines_dropped()
    {
        var lines = InstructionTemplates.Render("# Windows\r\n\r\nInstall-Module {id} -RequiredVersion {version} -Repository {feed}\n", "unused", Values);

        Assert.Equal([new InstructionLine("Windows", true), new InstructionLine("Install-Module Pester -RequiredVersion 5.7.1 -Repository modules", false)], lines);
    }

    [Fact]
    public void An_unknown_placeholder_stays_visible()
    {
        var line = Assert.Single(InstructionTemplates.Render("get {id} from {sourec}", "unused", Values));

        Assert.Equal("get Pester from {sourec}", line.Text);
    }

    [Fact]
    public void An_empty_template_is_the_default()
    {
        var lines = InstructionTemplates.Render("  ", InstructionTemplates.DefaultPackage, Values);

        Assert.Equal(3, lines.Count);
        Assert.StartsWith("Install-Module -Name Pester -RequiredVersion 5.7.1 -Repository modules", lines[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_typed_back_is_stored_as_the_default()
    {
        Assert.Null(InstructionTemplates.Normalize(InstructionTemplates.DefaultFeed.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n", InstructionTemplates.DefaultFeed));
        Assert.Null(InstructionTemplates.Normalize("", InstructionTemplates.DefaultFeed));
        Assert.Equal("curl {folderUrl}x", InstructionTemplates.Normalize(" curl {folderUrl}x ", InstructionTemplates.DefaultFiles));
    }

    /// <summary>Each purpose has its own commands and gallery; any client keeps the combined defaults and no gallery.</summary>
    [Fact]
    public void Each_purpose_has_its_own_commands_and_gallery()
    {
        Assert.Equal(InstructionTemplates.DefaultFeed, InstructionTemplates.DefaultFeedFor(FiGet.Domain.Entities.FeedPurpose.Any));
        Assert.Equal(InstructionTemplates.DefaultPackage, InstructionTemplates.DefaultPackageFor(FiGet.Domain.Entities.FeedPurpose.Any));
        Assert.Null(FiGet.Domain.Entities.FeedUpstream.KnownFor(FiGet.Domain.Entities.FeedPurpose.Any));

        var purposes = new[] { FiGet.Domain.Entities.FeedPurpose.PowerShell, FiGet.Domain.Entities.FeedPurpose.NuGet, FiGet.Domain.Entities.FeedPurpose.Chocolatey };
        Assert.Equal(4, purposes.Select(InstructionTemplates.DefaultFeedFor).Append(InstructionTemplates.DefaultFeed).Distinct().Count());
        Assert.Equal(4, purposes.Select(InstructionTemplates.DefaultPackageFor).Append(InstructionTemplates.DefaultPackage).Distinct().Count());
        Assert.Equal(
            ["PowerShell Gallery", "nuget.org", "Chocolatey community"],
            purposes.Select(p => FiGet.Domain.Entities.FeedUpstream.KnownFor(p)!.Name));
    }
}
