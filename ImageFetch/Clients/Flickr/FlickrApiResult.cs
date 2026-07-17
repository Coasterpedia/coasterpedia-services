namespace CoasterpediaServices.ImageFetch.Clients.Flickr;

/// <summary>
/// Flickr's error responses (e.g. bad api_key, unknown photo id) come back as
/// {"stat":"fail","message":"..."} with no payload - guard every call so that shows up as a
/// clean ImageFetchException instead of a NullReferenceException on the missing payload.
/// </summary>
internal static class FlickrApiResult
{
    public static T EnsureOk<T>(string stat, string? message, T? payload) where T : class
    {
        if (stat != "ok" || payload == null)
        {
            throw new ImageFetchException(502, $"Flickr API error: {message ?? "unknown error"}");
        }

        return payload;
    }
}
