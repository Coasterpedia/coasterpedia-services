using System.Text.RegularExpressions;
using CoasterpediaServices.ImageFetch.Clients.Commons;
using CoasterpediaServices.ImageFetch.Provenance;
using Microsoft.Extensions.Logging;
using WikiClientLibrary.Pages;
using WikiClientLibrary.Pages.Queries;
using WikiClientLibrary.Pages.Queries.Properties;

namespace CoasterpediaServices.ImageFetch.Fetchers;

public class CommonsFetcher : ISourceFetcher
{
    private const string FilePathPrefix = "/wiki/File:";

    private static readonly Regex FlickrUrlPattern = new(
        @"https?://(?:www\.|m\.)?flickr\.com/photos/[^\s""'<>]+",
        RegexOptions.Compiled);

    private static readonly Regex PanoramioUrlPattern = new(
        @"https?://(?:www\.)?panoramio\.com/[^\s""'<>]+",
        RegexOptions.Compiled);

    private static readonly Regex GeographUrlPattern = new(
        @"https?://(?:www\.)?geograph\.org\.uk/photo/[^\s""'<>]+",
        RegexOptions.Compiled);

    private readonly CommonsSiteAccessor _siteAccessor;
    private readonly CommonsClient _commonsClient;
    private readonly FlickrFetcher _flickrFetcher;
    private readonly GeographFetcher _geographFetcher;
    private readonly ILogger<CommonsFetcher> _logger;

    public CommonsFetcher(CommonsSiteAccessor siteAccessor, CommonsClient commonsClient, FlickrFetcher flickrFetcher,
        GeographFetcher geographFetcher, ILogger<CommonsFetcher> logger)
    {
        _siteAccessor = siteAccessor;
        _commonsClient = commonsClient;
        _flickrFetcher = flickrFetcher;
        _geographFetcher = geographFetcher;
        _logger = logger;
    }

    public bool CanHandle(Uri uri) => uri.Host == "commons.wikimedia.org";

    public async Task<FetchResult> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!uri.AbsolutePath.StartsWith(FilePathPrefix, StringComparison.Ordinal))
        {
            throw new ImageFetchException(400,
                "Unrecognised Wikimedia Commons URL, please link to a file page (commons.wikimedia.org/wiki/File:Example.jpg).");
        }

        var commonsSite = await _siteAccessor.GetCommonsAsync();
        var filename = Uri.UnescapeDataString(Uri.UnescapeDataString(uri.AbsolutePath[FilePathPrefix.Length..]));

        var page = new WikiPage(commonsSite, $"File:{filename}");
        await page.RefreshAsync(new WikiPageQueryProvider
        {
            Properties = { new FileInfoPropertyProvider { QueryExtMetadata = true } }
        }, cancellationToken);

        if (!page.Exists
            || page.GetPropertyGroup<FileInfoPropertyGroup>() is not { } fileInfoGroup
            || fileInfoGroup.LatestRevision is not { } fileInfo)
        {
            throw new ImageFetchException(404, "File not found on Wikimedia Commons.");
        }

        var extMetadata = fileInfo.ExtMetadata;

        // Chase one mirror hop: a Commons file that's actually a Flickr or Geograph mirror should
        // credit the original and pull from there, not Commons' re-encoded copy.
        string? flickrSourceUrl = null;
        string? geographSourceUrl = null;
        string? panoramioSourceUrl = null;
        foreach (var key in new[] { "Credit", "ImageDescription", "Source" })
        {
            if (extMetadata.TryGetValue(key, out var value))
            {
                var text = value.Value.ToString();

                var flickrMatch = FlickrUrlPattern.Match(text);
                if (flickrMatch.Success)
                {
                    flickrSourceUrl = flickrMatch.Value;
                    break;
                }

                var geographMatch = GeographUrlPattern.Match(text);
                if (geographMatch.Success)
                {
                    geographSourceUrl = geographMatch.Value;
                    break;
                }

                var panoramioMatch = PanoramioUrlPattern.Match(text);
                if (panoramioMatch.Success)
                {
                    panoramioSourceUrl = panoramioMatch.Value;
                    break;
                }
            }
        }

        if (flickrSourceUrl != null)
        {
            try
            {
                var chased = await _flickrFetcher.FetchAsync(new Uri(flickrSourceUrl), cancellationToken);
                return chased with { OriginUrl = fileInfo.DescriptionUrl };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The chase is a nice-to-have (better credit/GPS than Commons' re-encoded copy) -
                // if the origin site is down or blocking us, fall back to Commons' own copy below
                // rather than failing the whole fetch.
                _logger.LogWarning(ex, "Failed to chase Flickr mirror {FlickrUrl} for {CommonsUrl}, falling back to Commons copy",
                    flickrSourceUrl, uri);
            }
        }

        if (geographSourceUrl != null)
        {
            try
            {
                var chased = await _geographFetcher.FetchAsync(new Uri(geographSourceUrl), cancellationToken);
                return chased with { OriginUrl = fileInfo.DescriptionUrl };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to chase Geograph mirror {GeographUrl} for {CommonsUrl}, falling back to Commons copy",
                    geographSourceUrl, uri);
            }
        }

        if (!extMetadata.TryGetValue("License", out var license) || string.IsNullOrWhiteSpace(license.Value.ToString()))
        {
            throw new ImageFetchException(422, "Unrecognised or missing license on Commons file.");
        }

        var bytes = await BoundedDownloader.DownloadAsync(_commonsClient.HttpClient, fileInfo.Url, cancellationToken);

        string? date = null;
        if (extMetadata.TryGetValue("DateTimeOriginal", out var taken))
        {
            date = taken.Value.ToString();
        }
        else if (extMetadata.TryGetValue("DateTime", out var uploaded))
        {
            date = uploaded.Value.ToString();
        }

        string? latitude = null;
        string? longitude = null;
        if (extMetadata.TryGetValue("GPSLatitude", out var lat) && extMetadata.TryGetValue("GPSLongitude", out var lon))
        {
            latitude = lat.Value.ToString();
            longitude = lon.Value.ToString();
        }

        var extension = Path.GetExtension(filename);

        // Panoramio is dead, so its card links back to the Commons page rather than the
        // now-404ing panoramio.com URL.
        var extraCards = panoramioSourceUrl != null
            ? new[] { $"{{{{Panoramio|{fileInfo.DescriptionUrl}}}}}" }
            : null;
        var slug = license.Value.ToString().Trim().ToLowerInvariant();
        var provenance = ProvenanceBuilder.Build(SourceRegistry.Commons, slug, fileInfo.DescriptionUrl, extraCards);

        return new FetchResult
        {
            Bytes = bytes,
            ContentType = MimeTypes.FromExtension(extension),
            SuggestedFileName = filename,
            Title = filename[..^extension.Length].Replace('_', ' '),
            Author = fileInfo.UserName,
            SourceUrl = fileInfo.DescriptionUrl,
            Source = provenance.Source,
            License = provenance.License,
            Cards = provenance.Cards,
            Date = date,
            Latitude = latitude,
            Longitude = longitude
        };
    }
}
