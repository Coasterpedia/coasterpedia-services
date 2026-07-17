using CoasterpediaServices.ImageFetch.Options;
using Microsoft.Extensions.Options;

namespace CoasterpediaServices.ImageFetch.Clients.Flickr;

/// <summary>
/// Flickr's licence id -&gt; name list is effectively static, so it's fetched once and cached
/// for the process lifetime rather than on every photo fetch.
/// </summary>
public class FlickrLicenseCache
{
    private readonly IFlickrClient _flickrClient;
    private readonly FlickrConfig _config;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<int, string>? _namesById;

    public FlickrLicenseCache(IFlickrClient flickrClient, IOptions<FlickrConfig> config)
    {
        _flickrClient = flickrClient;
        _config = config.Value;
    }

    public async Task<string?> GetLicenseNameAsync(int licenseId, CancellationToken cancellationToken)
    {
        var names = await GetAllAsync(cancellationToken);
        return names.GetValueOrDefault(licenseId);
    }

    private async Task<Dictionary<int, string>> GetAllAsync(CancellationToken cancellationToken)
    {
        if (_namesById != null)
        {
            return _namesById;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_namesById == null)
            {
                var envelope = await _flickrClient.GetLicensesAsync(_config.ApiKey);
                var licenses = FlickrApiResult.EnsureOk(envelope.Stat, envelope.Message, envelope.Licenses);
                _namesById = licenses.License.ToDictionary(l => l.Id, l => l.Name);
            }

            return _namesById;
        }
        finally
        {
            _lock.Release();
        }
    }
}
