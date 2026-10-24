namespace CoasterpediaServices.ArchiveBot.Clients.Wayback;

public static class WaybackClientExtensions
{
    // Successful captures only, as InternetArchiveBot does: a 404 or redirect capture isn't the cited page.
    private const string SuccessFilter = "statuscode:20[036]";

    // Picks the capture that shows what the citation saw: the latest on or before the access date, else
    // the first after it. Without one, the first capture after publication, then the latest of all. The
    // latest alone is often a parked domain or an error page that returned 200.
    public static async Task<Snapshot?> FindSnapshot(this IWaybackClient client, string url, DateTime? accessDate, DateTime? publishDate)
    {
        if (accessDate is { } accessed)
        {
            var endOfDay = accessed.ToString("yyyyMMdd") + "235959";
            return await Latest(client, url, null, endOfDay) ?? await First(client, url, endOfDay);
        }

        if (publishDate is { } published)
        {
            return await First(client, url, published.ToString("yyyyMMdd")) ?? await Latest(client, url, null, null);
        }

        return await Latest(client, url, null, null);
    }

    private static async Task<Snapshot?> First(IWaybackClient client, string url, string? from) =>
        ToSnapshot(url, await client.GetCaptures(url, from, null, 1, SuccessFilter));

    private static async Task<Snapshot?> Latest(IWaybackClient client, string url, string? from, string? to) =>
        ToSnapshot(url, await client.GetCaptures(url, from, to, -1, SuccessFilter));

    private static Snapshot? ToSnapshot(string url, List<List<string>>? rows)
    {
        var timestamp = rows?.Skip(1).FirstOrDefault()?.FirstOrDefault();
        return timestamp == null
            ? null
            : new Snapshot($"https://web.archive.org/web/{timestamp}/{url}", DateTime.ParseExact(timestamp, "yyyyMMddHHmmss", null));
    }
}

public record Snapshot(string Url, DateTime Captured);
