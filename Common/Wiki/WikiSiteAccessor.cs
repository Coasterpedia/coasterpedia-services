using CoasterpediaServices.Common.Options;
using Microsoft.Extensions.Options;
using WikiClientLibrary.Sites;

namespace CoasterpediaServices.Common.Wiki;

public class WikiSiteAccessor
{
    private readonly IOptions<CoasterpediaConfig> _coasterpediaConfig;
    private readonly CoasterpediaClient _coasterpediaClient;
    private WikiSite? _coasterpediaSite;

    public WikiSiteAccessor(CoasterpediaClient coasterpediaClient, IOptions<CoasterpediaConfig> coasterpediaConfig)
    {
        _coasterpediaClient = coasterpediaClient;
        _coasterpediaConfig = coasterpediaConfig;
    }

    public async Task<WikiSite> GetCoasterpedia(string botUsername, string botPassword)
    {
        if (_coasterpediaSite == null)
        {
            _coasterpediaSite = new WikiSite(_coasterpediaClient.GetSite(), _coasterpediaConfig.Value.BaseUrl + "/w/api.php");
            await _coasterpediaSite.Initialization;
            await _coasterpediaSite.LoginAsync(botUsername, botPassword);
        }

        return _coasterpediaSite;
    }
}