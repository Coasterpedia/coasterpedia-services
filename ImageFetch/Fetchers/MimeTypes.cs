namespace CoasterpediaServices.ImageFetch.Fetchers;

internal static class MimeTypes
{
    public static string FromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".tif" or ".tiff" => "image/tiff",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream"
    };
}
