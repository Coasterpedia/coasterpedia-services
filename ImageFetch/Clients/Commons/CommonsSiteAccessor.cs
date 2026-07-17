using WikiClientLibrary.Sites;

namespace CoasterpediaServices.ImageFetch.Clients.Commons;

/// <summary>
/// Anonymous (read-only) access to the Commons WikiSite - no login, this service never writes to the wiki.
/// </summary>
public class CommonsSiteAccessor
{
    private readonly CommonsClient _commonsClient;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private WikiSite? _commonsSite;

    public CommonsSiteAccessor(CommonsClient commonsClient)
    {
        _commonsClient = commonsClient;
    }

    public async Task<WikiSite> GetCommonsAsync()
    {
        if (_commonsSite != null)
        {
            return _commonsSite;
        }

        await _lock.WaitAsync();
        try
        {
            if (_commonsSite == null)
            {
                var site = new WikiSite(_commonsClient.GetSite(), "https://commons.wikimedia.org/w/api.php");
                await site.Initialization;
                _commonsSite = site;
            }

            return _commonsSite;
        }
        finally
        {
            _lock.Release();
        }
    }
}
