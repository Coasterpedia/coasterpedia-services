using System.Text;
using System.Text.Json;
using CoasterpediaServices.ImageFetch.Auth;
using CoasterpediaServices.ImageFetch.Fetchers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace CoasterpediaServices.ImageFetch;

public record ImageFetchRequest(string Url, string? CsrfToken);

public record ImageFetchCollectionResponse(IReadOnlyList<CollectionItem> Items);

[ApiController]
[Route("imagefetch")]
[ServiceFilter(typeof(SameOriginCsrfFilter))]
[ServiceFilter(typeof(WikiUserInfoGateFilter))]
public class ImageFetchController : ControllerBase
{
    private readonly ImageFetchDispatcher _dispatcher;
    private readonly ImageFetchCollectionDispatcher _collectionDispatcher;
    private readonly ILogger<ImageFetchController> _logger;

    public ImageFetchController(
        ImageFetchDispatcher dispatcher,
        ImageFetchCollectionDispatcher collectionDispatcher,
        ILogger<ImageFetchController> logger)
    {
        _dispatcher = dispatcher;
        _collectionDispatcher = collectionDispatcher;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] ImageFetchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CsrfToken))
        {
            return Problem(detail: "Missing CSRF token.", statusCode: StatusCodes.Status403Forbidden);
        }

        if (string.IsNullOrWhiteSpace(request.Url))
        {
            return Problem(detail: "Missing url.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            return Problem(detail: "Invalid URL.", statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            // A collection (Flickr album, Commons category) resolves to a list of single-item
            // URLs instead of bytes - the response shape (JSON vs. image bytes) is the only
            // signal the caller needs, so it never has to classify the URL itself.
            if (_collectionDispatcher.CanHandle(uri))
            {
                var items = await _collectionDispatcher.FetchAsync(uri, HttpContext.RequestAborted);
                return Ok(new ImageFetchCollectionResponse(items));
            }

            var result = await _dispatcher.FetchAsync(request.Url, HttpContext.RequestAborted);

            var metadataJson = JsonSerializer.Serialize(new
            {
                result.Title,
                result.Author,
                result.SourceUrl,
                result.Source,
                result.License,
                result.Cards,
                result.Date,
                result.Latitude,
                result.Longitude,
                result.OriginUrl,
                result.SuggestedFileName
            }, JsonSerializerOptions.Web);
            Response.Headers["X-ImageFetch-Metadata"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(metadataJson));

            return File(result.Bytes, result.ContentType);
        }
        catch (ImageFetchException ex)
        {
            return Problem(detail: ex.Message, statusCode: ex.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error fetching {Url}", request.Url);
            return Problem(detail: "Failed to fetch the source image.", statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
