using CoasterpediaServices.Common.Options;
using Microsoft.Extensions.Options;
using WikiClientLibrary.Sites;

namespace CoasterpediaServices.Common.Wiki;

public class WikiSiteAccessor
{
    private readonly IOptions<CoasterpediaConfig> _coasterpediaConfig;
    private readonly CoasterpediaClient _coasterpediaClient;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private WikiSite? _coasterpediaSite;

    public WikiSiteAccessor(CoasterpediaClient coasterpediaClient, IOptions<CoasterpediaConfig> coasterpediaConfig)
    {
        _coasterpediaClient = coasterpediaClient;
        _coasterpediaConfig = coasterpediaConfig;
    }

    public async Task<WikiSite> GetCoasterpedia(string botUsername, string botPassword)
    {
        if (_coasterpediaSite != null)
        {
            return _coasterpediaSite;
        }

        await _lock.WaitAsync();
        try
        {
            if (_coasterpediaSite != null)
            {
                return _coasterpediaSite;
            }

            var site = new WikiSite(_coasterpediaClient.GetSite(), _coasterpediaConfig.Value.BaseUrl + "/w/api.php");
            await site.Initialization;
            await site.LoginAsync(botUsername, botPassword);
            _coasterpediaSite = site;
        }
        finally
        {
            _lock.Release();
        }

        return _coasterpediaSite;
    }
}