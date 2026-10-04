using System.Xml.Linq;
using CoasterpediaServices.ImageFetch.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoasterpediaServices.ImageFetch.Clients.GeographDe;

/// <summary>
/// Reads one photo from the Geograph Deutschland API.
///
/// Hand-rolled rather than Refit because this endpoint is read as XML, not JSON. That is not a
/// style choice: the site is ISO-8859-1 and its JSON encoder mangles every non-ASCII character —
/// "Mühlacker" comes back as "M?0068006c00610063006ber", which on a German site means most titles.
/// The XML branch of the same endpoint emits clean numeric character references
/// (<c>M&amp;#252;hlacker</c>), so it is the only usable one.
/// </summary>
public sealed class GeographDeClient : IGeographDeClient
{
    private readonly HttpClient _httpClient;
    private readonly GeographDeConfig _config;
    private readonly ILogger<GeographDeClient> _logger;

    public GeographDeClient(HttpClient httpClient, IOptions<GeographDeConfig> config,
        ILogger<GeographDeClient> logger)
    {
        _httpClient = httpClient;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<GeographDePhoto> GetPhotoAsync(string photoId, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"/api/photo/{photoId}/{_config.ApiKey}?format=xml", cancellationToken);
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
            throw NotXml(response, body);
        }

        var root = document.Root ?? throw NotXml(response, body);

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

    /// <summary>
    /// The response wasn't the XML we asked for. That is worth logging with its body: the one
    /// failure seen in the wild is a 403 refusal served as an HTML page, and the page says why.
    /// The body is logged, never surfaced — it can be a full error page.
    /// </summary>
    private ImageFetchException NotXml(HttpResponseMessage response, string body)
    {
        var snippet = body.Length > 500 ? body[..500] : body;
        _logger.LogWarning(
            "Geograph Deutschland returned {StatusCode} and a non-XML body (key {KeyState}). First 500 chars: {Body}",
            (int)response.StatusCode,
            string.IsNullOrEmpty(_config.ApiKey) ? "absent" : "present",
            snippet);

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return new ImageFetchException(502,
                "Geograph Deutschland refused the request. It may need an API key from this server's address.");
        }

        return new ImageFetchException(502,
            response.IsSuccessStatusCode
                ? "Geograph Deutschland returned a response we couldn't read."
                : $"Geograph Deutschland returned {(int)response.StatusCode}.");
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
