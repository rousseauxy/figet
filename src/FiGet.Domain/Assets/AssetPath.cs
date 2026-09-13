namespace FiGet.Domain.Assets;

/// <summary>
/// A validated path inside an asset directory. Every way a path reaches the server - a URL, a form, a
/// dropped file - goes through <see cref="TryParse"/>, so what counts as a valid path is decided once.
/// </summary>
public sealed class AssetPath
{
    /// <summary>The longest full path accepted. Sized to stay inside a SQL Server index key.</summary>
    public const int MaxLength = 400;

    /// <summary>The longest single name accepted, which is what common file systems allow.</summary>
    public const int MaxSegmentLength = 255;

    private AssetPath(IReadOnlyList<string> segments)
    {
        Segments = segments;
        Value = string.Join('/', segments);
        Lower = Value.ToLowerInvariant();
    }

    /// <summary>The empty path: the root folder of the directory.</summary>
    public static AssetPath Root { get; } = new([]);

    public IReadOnlyList<string> Segments { get; }

    /// <summary>Segments joined by <c>/</c>, without a leading or trailing slash.</summary>
    public string Value { get; }

    public string Lower { get; }

    public bool IsRoot => Segments.Count == 0;

    /// <summary>The last segment; empty for the root.</summary>
    public string Name => IsRoot ? "" : Segments[^1];

    /// <summary>The containing folder; the root's parent is the root.</summary>
    public AssetPath Parent => IsRoot ? this : new AssetPath([.. Segments.Take(Segments.Count - 1)]);

    /// <summary>Every folder above this path, nearest the root first, not including the root itself.</summary>
    public IEnumerable<AssetPath> Ancestors()
    {
        for (var count = 1; count < Segments.Count; count++)
        {
            yield return new AssetPath([.. Segments.Take(count)]);
        }
    }

    public AssetPath Child(string name) =>
        TryParse(IsRoot ? name : Value + "/" + name, out var child) && child.Segments.Count == Segments.Count + 1
            ? child
            : throw new ArgumentException($"'{name}' is not a valid single name.", nameof(name));

    /// <summary>
    /// Parses a path. Empty segments are dropped, so <c>a//b/</c> is <c>a/b</c> - clients build these by
    /// concatenation and a doubled slash is an accident, not a request for a folder with no name. Refused:
    /// <c>.</c> and <c>..</c>, backslashes, control characters, and anything too long. Null or empty is the root.
    /// </summary>
    public static bool TryParse(string? text, out AssetPath path)
    {
        path = Root;
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            // A backslash is a separator to a Windows client and an ordinary character to everyone else,
            // so accepting it would let one name mean two different paths depending on who reads it.
            if (segment is "." or ".."
                || segment.Length > MaxSegmentLength
                || segment.Contains('\\', StringComparison.Ordinal)
                || segment.Any(char.IsControl)
                || segment.Trim().Length == 0)
            {
                return false;
            }
        }

        var candidate = new AssetPath(segments);
        if (candidate.Value.Length > MaxLength)
        {
            return false;
        }

        path = candidate;
        return true;
    }

    public override string ToString() => Value;
}
