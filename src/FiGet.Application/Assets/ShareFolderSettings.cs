namespace FiGet.Application.Assets;

/// <summary>
/// Where the shares mount is (<c>FiGet:Assets:SharesRoot</c>): the folder whose direct sub-folders an administrator may
/// choose as the content of an asset directory. Bound by the host, like <c>TempFileSettings</c>, so the resolver does not
/// read configuration itself. Null or blank: the pages offer no folders.
/// </summary>
public sealed class ShareFolderSettings
{
    public string? Root { get; set; }
}
