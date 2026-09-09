using System.Xml.Linq;

namespace CoasterpediaServices.ImageFetch.Clients.GeographDe;

/// <summary>
/// Reads one photo from the Geograph Deutschland API.
///
/// Hand-rolled rather than Refit because this endpoint is read as XML, not JSON. That is not a
/// style choice: the site is ISO-8859-1 and its JSON encoder mangles every non-ASCII character —
/// "Mühlacker" comes back as "M?0068006c00610063006ber", which on a German site means most titles.
/// The XML branch of the same endpoint emits clean numeric character references
/// (<c>M&amp;#252;hlacker</c>), so it is the only usable one. No API key: the key path segment is
/// optional here, unlike its British sibling.
/// </summary>
public sealed class GeographDeClient : IGeographDeClient
{
    private readonly HttpClient _httpClient;

    public GeographDeClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<GeographDePhoto> GetPhotoAsync(string photoId, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync($"/api/photo/{photoId}?format=xml", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        // Read the body before the status code, not after: a bad photo id comes back as HTTP 400
        // carrying a perfectly good <status state="failed"> explaining itself, and that message is
        // the one worth showing. Only an unreadable body falls back to the status code.
        XDocument document;
        try
        {
            document = XDocument.Parse(body);
        }
        catch (System.Xml.XmlException)
        {
            throw new ImageFetchException(502,
                response.IsSuccessStatusCode
                    ? "Geograph Deutschland returned a response we couldn't read."
                    : $"Geograph Deutschland returned {(int)response.StatusCode}.");
        }

        var root = document.Root
                   ?? throw new ImageFetchException(502, "Geograph Deutschland returned an empty response.");

        // <status state="failed"><error code="400"><message>Invalid image id 9999</message>
        var status = root.Element("status");
        if (status?.Attribute("state")?.Value != "ok")
        {
            var message = status?.Element("error")?.Element("message")?.Value;
            return new GeographDePhoto { Error = message ?? "Photo not found on Geograph Deutschland." };
        }

        var location = root.Element("location");

        return new GeographDePhoto
        {
            Title = Trimmed(root.Element("title")?.Value),
            Author = Trimmed(root.Element("user")?.Value),
            ImageUrl = Trimmed(root.Element("img")?.Attribute("src")?.Value),
            Taken = Trimmed(root.Element("taken")?.Value),
            Latitude = Trimmed(location?.Attribute("lat")?.Value),
            Longitude = Trimmed(location?.Attribute("long")?.Value)
        };
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
