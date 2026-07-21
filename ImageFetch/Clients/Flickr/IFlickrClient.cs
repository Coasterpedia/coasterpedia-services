using Refit;

namespace CoasterpediaServices.ImageFetch.Clients.Flickr;

public interface IFlickrClient
{
    [Get("/services/rest/?method=flickr.photos.getInfo&format=json&nojsoncallback=1")]
    Task<FlickrPhotoInfoEnvelope> GetPhotoInfoAsync([AliasAs("api_key")] string apiKey, [AliasAs("photo_id")] string photoId);

    [Get("/services/rest/?method=flickr.photos.getSizes&format=json&nojsoncallback=1")]
    Task<FlickrPhotoSizesEnvelope> GetPhotoSizesAsync([AliasAs("api_key")] string apiKey, [AliasAs("photo_id")] string photoId);

    [Get("/services/rest/?method=flickr.photos.licenses.getInfo&format=json&nojsoncallback=1")]
    Task<FlickrLicensesEnvelope> GetLicensesAsync([AliasAs("api_key")] string apiKey);

    // extras=url_s: the same square/small-thumbnail URL UploadWizard's mw.FlickrChecker
    // uses for its selection grid - a public Flickr CDN URL, no auth needed to hotlink.
    // extras=license: lets the album listing filter out unusable licences without an
    // extra getInfo call per photo, matching mw.FlickrChecker's getPhotos behaviour.
    // per_page is capped at 500 by Flickr, so a larger album can only be reached by
    // walking `page` - the caller pages until it has enough USABLE (free-licensed)
    // photos, since the licence filter below would otherwise silently eat most of a
    // single page.
    [Get("/services/rest/?method=flickr.photosets.getPhotos&format=json&nojsoncallback=1&extras=url_s,license")]
    Task<FlickrPhotosetPhotosEnvelope> GetPhotosetPhotosAsync(
        [AliasAs("api_key")] string apiKey,
        [AliasAs("photoset_id")] string photosetId,
        [AliasAs("per_page")] int perPage,
        [AliasAs("page")] int page,
        CancellationToken cancellationToken = default);
}
