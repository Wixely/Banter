namespace Banter.App;

/// <summary>
/// Minimal extension-to-MIME map for uploads. Deliberately small: the server stores whatever it
/// is told and the type is only a hint for rendering, so a wrong guess costs a preview, not
/// correctness. Anything unrecognised is <c>application/octet-stream</c>.
/// </summary>
public static class MimeTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".log"] = "text/plain",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".yaml"] = "application/yaml",
        [".yml"] = "application/yaml",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".gz"] = "application/gzip",
        [".wav"] = "audio/wav",
        [".mp3"] = "audio/mpeg",
        [".opus"] = "audio/opus",
        [".webm"] = "video/webm",
        [".mp4"] = "video/mp4",
        [".cs"] = "text/plain",
    };

    public static string ForFile(string fileName) =>
        ByExtension.GetValueOrDefault(Path.GetExtension(fileName), "application/octet-stream");

    /// <summary>True for types a timeline could render inline rather than as a download.</summary>
    public static bool IsImage(string mimeType) =>
        mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}
