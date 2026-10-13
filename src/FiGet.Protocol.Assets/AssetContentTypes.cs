using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace FiGet.Protocol.Assets;

/// <summary>Decides the content type a stored file is served with.</summary>
public static class AssetContentTypes
{
    public const string Fallback = "application/octet-stream";

    private static readonly FileExtensionContentTypeProvider ByExtension = new();

    /// <summary>
    /// The type the uploader sent, unless it says nothing about the file: then the extension decides.
    ///
    /// Two sent types are ignored on purpose. <c>application/x-www-form-urlencoded</c> is what curl labels
    /// <c>--data-binary</c> with by default, so honouring it would serve every file uploaded the documented
    /// way as a web form. <c>application/octet-stream</c> is what a client sends when it does not know, and
    /// the extension usually does - as is <c>binary/octet-stream</c>, a non-standard spelling of the same
    /// shrug that some storage services and CDNs serve, and that a file fetched by URL arrives with.
    /// </summary>
    public static string Resolve(string? sent, string fileName)
    {
        if (MediaTypeHeaderValue.TryParse(sent, out var parsed)
            && parsed.MediaType.HasValue
            && !parsed.MediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
            && !parsed.MediaType.Equals(Fallback, StringComparison.OrdinalIgnoreCase)
            && !parsed.MediaType.Equals("binary/octet-stream", StringComparison.OrdinalIgnoreCase)
            && !parsed.MediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return parsed.ToString();
        }

        return ByExtension.TryGetContentType(fileName, out var guessed) ? guessed : Fallback;
    }
}
