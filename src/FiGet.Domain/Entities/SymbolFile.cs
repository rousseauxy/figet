namespace FiGet.Domain.Entities;

/// <summary>A portable PDB extracted from a symbol package, addressable by the symbol server key.</summary>
public sealed class SymbolFile
{
    public long Key { get; set; }

    public long PackageVersionKey { get; set; }

    public PackageVersion? PackageVersion { get; set; }

    public int FeedKey { get; set; }

    /// <summary>Lower-cased PDB file name, for example <c>mylib.pdb</c>.</summary>
    public required string FileNameLower { get; set; }

    /// <summary>Lower-cased symbol key: the 32 hex digits of the PDB id followed by <c>ffffffff</c>.</summary>
    public required string SymbolKeyLower { get; set; }

    public long Size { get; set; }
}
