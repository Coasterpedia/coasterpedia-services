namespace CoasterpediaServices.ImageFetch.Fetchers;

/// <summary>
/// Downloads with a hard byte cap enforced while streaming, matching the 32 MB bound the
/// browser-side bulk fetch/stash/publish loop relies on (PHP's max upload/post size).
/// </summary>
internal static class BoundedDownloader
{
    public const long MaxBytes = 32 * 1024 * 1024;

    public static async Task<byte[]> DownloadAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ImageFetchException(502, $"Failed to download source file ({(int)response.StatusCode}).");
        }

        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > MaxBytes)
        {
            throw new ImageFetchException(413, "Source file exceeds the 32 MB limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxBytes)
            {
                throw new ImageFetchException(413, "Source file exceeds the 32 MB limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return buffer.ToArray();
    }
}
