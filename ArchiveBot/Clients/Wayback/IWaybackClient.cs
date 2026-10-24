using Refit;

namespace CoasterpediaServices.ArchiveBot.Clients.Wayback;

public interface IWaybackClient
{
    // Rows of [timestamp] after a header row. A negative limit returns the last captures rather than the first.
    [Get("/cdx/search/cdx?output=json&fl=timestamp")]
    public Task<List<List<string>>?> GetCaptures(string url, [AliasAs("from")] string? fromTimestamp, [AliasAs("to")] string? toTimestamp,
        int limit, string filter);
}
