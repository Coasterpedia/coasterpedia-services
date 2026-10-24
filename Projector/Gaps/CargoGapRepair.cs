using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CoasterpediaServices.Common.Wiki;
using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Jobs;
using CoasterpediaServices.Projector.Options;
using CoasterpediaServices.Projector.Vocabulary;
using Dapper;
using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using WikiClientLibrary.Pages;

namespace CoasterpediaServices.Projector.Gaps;

/// <summary>
/// The account the null edits use. WikiSiteAccessor keeps one signed-in site
/// per process, whichever login reaches it first, so this has to be
/// ArchiveBot's: it is bound from ArchiveBotConfig.
/// </summary>
public sealed record GapBotLogin
{
    public string BotUsername { get; init; } = "";
    public string BotPassword { get; init; } = "";
}

/// <summary>An article transcluding an infobox with no row in that infobox's table, or none in _pageData.</summary>
public sealed record CargoGap(int PageId, string Title, IReadOnlyList<string> MissingTables, bool MissingPageData)
{
    public string Missing => string.Join(", ", MissingPageData ? [.. MissingTables, "_pageData"] : MissingTables);
}

/// <summary>
/// Finds articles whose infobox stored no Cargo row, and null-edits them. A
/// missing row drops the article from every list, navplate and count, and the
/// nightly sweep deletes its API record. Saves while a __NEXT table exists and
/// a value too long for its column are the known causes; a null edit fixes the
/// first and not the second. Purging with forcelinkupdate doesn't store rows
/// (tested 2026-10-24); only a save does.
/// </summary>
public sealed partial class CargoGapRepair
{
    private const string Summary = "Null edit to restore the article's Cargo data";

    // Saves have finished storing well within this (they store in post-send deferred updates).
    private static readonly TimeSpan StoreWait = TimeSpan.FromSeconds(15);

    private readonly CargoReader _reader;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WikiSiteAccessor _sites;
    private readonly ProjectorConfig _config;
    private readonly GapBotLogin _login;
    private readonly ILogger<CargoGapRepair> _logger;

    public CargoGapRepair(CargoReader reader, IHttpClientFactory httpClientFactory, WikiSiteAccessor sites,
        IOptions<ProjectorConfig> config, IOptions<GapBotLogin> login, ILogger<CargoGapRepair> logger)
    {
        _reader = reader;
        _httpClientFactory = httpClientFactory;
        _sites = sites;
        _config = config.Value;
        _login = login.Value;
        _logger = logger;
        foreach (var table in Options.Tables.Keys)
        {
            if (!IdentifierPattern().IsMatch(table))
            {
                throw new InvalidOperationException($"Not a plain SQL identifier: {table}");
            }
        }
    }

    private CargoGapOptions Options => _config.Gaps;

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex IdentifierPattern();

    /// <summary>
    /// Fails the job whenever a gap is left, so the dashboard shows it: too
    /// many to edit, a null edit that didn't bring the row back, or report-only.
    /// </summary>
    [Queue(ProjectorJobs.Queue)]
    [AutomaticRetry(Attempts = 0)]
    public async Task Run()
    {
        var ct = CancellationToken.None;
        await using var conn = await _reader.OpenAsync(ct);

        var rebuilding = await RebuildingTablesAsync(conn, ct);
        if (rebuilding.Count > 0)
        {
            _logger.LogWarning("Cargo gap check skipped: {Tables} exist, so saves write there and gaps are expected",
                string.Join(", ", rebuilding));
            return;
        }

        var transcluding = new Dictionary<string, Dictionary<int, string>>();
        var stored = new Dictionary<string, HashSet<int>>();
        foreach (var (table, templates) in Options.Tables)
        {
            transcluding[table] = await TranscludingAsync(templates, ct);
            stored[table] = await StoredAsync(conn, table, null, ct);
        }

        var pageData = await StoredAsync(conn, "_pageData", null, ct);
        var gaps = FindGaps(transcluding, stored, pageData);

        // A page edited moments ago may still be storing; one deleted since has nothing to restore.
        var edits = await LastEditsAsync(gaps.Select(g => g.PageId).ToList(), ct);
        var cutoff = DateTimeOffset.UtcNow - Options.Grace;
        gaps = gaps.Where(g => edits.TryGetValue(g.PageId, out var e) && e.Timestamp < cutoff).ToList();

        var articles = transcluding.Values.SelectMany(p => p.Keys).Distinct().Count();
        if (gaps.Count == 0)
        {
            _logger.LogInformation("Cargo gap check: every one of {Articles} articles has its rows", articles);
            return;
        }

        foreach (var gap in gaps)
        {
            var edit = edits[gap.PageId];
            _logger.LogWarning("Cargo gap: {Title} ({PageId}) has no row in {Missing}; last edited {Timestamp:u} by {User}",
                gap.Title, gap.PageId, gap.Missing, edit.Timestamp, edit.User);
        }

        if (gaps.Count > Options.FixLimit)
        {
            throw new InvalidOperationException(Options.FixLimit == 0
                ? $"{gaps.Count} of {articles} articles have no Cargo row. FixLimit is 0, so none were edited."
                : $"{gaps.Count} of {articles} articles have no Cargo row, more than FixLimit ({Options.FixLimit}), " +
                  "so none were edited. That many points at a cause a null edit won't fix: look at the edit times above.");
        }

        var site = await _sites.GetCoasterpedia(_login.BotUsername, _login.BotPassword);
        foreach (var gap in gaps)
        {
            try
            {
                var page = new WikiPage(site, gap.Title);
                await page.RefreshAsync(PageQueryOptions.FetchContent);
                if (page.Id != gap.PageId || page.Content == null)
                {
                    _logger.LogWarning("Cargo gap: {Title} is no longer page {PageId}, skipped", gap.Title, gap.PageId);
                    continue;
                }

                if (await page.EditAsync(new WikiPageEditOptions { Content = page.Content, Summary = Summary, Bot = true, Minor = true }))
                {
                    _logger.LogWarning("Cargo gap: the null edit to {Title} changed the page", gap.Title);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Cargo gap: couldn't null-edit {Title} ({PageId})", gap.Title, gap.PageId);
            }

            await Task.Delay(Options.EditInterval, ct);
        }

        await Task.Delay(StoreWait, ct);
        var left = await RecheckAsync(conn, gaps, ct);
        foreach (var gap in left)
        {
            _logger.LogError("Cargo gap: {Title} ({PageId}) still has no row in {Missing} after a null edit. " +
                             "Its store fails on every save; look for a value over 300 characters",
                gap.Title, gap.PageId, gap.Missing);
        }

        _logger.LogInformation("Cargo gap check: restored {Restored} of {Gaps}", gaps.Count - left.Count, gaps.Count);
        if (left.Count > 0)
        {
            throw new InvalidOperationException($"{left.Count} article(s) still have no Cargo row after a null edit: " +
                                                string.Join(", ", left.Select(g => g.Title)));
        }
    }

    /// <summary>
    /// Every page that transcludes a table's infobox and lacks a row there, or
    /// lacks one in _pageData, in page ID order.
    /// </summary>
    internal static List<CargoGap> FindGaps(IReadOnlyDictionary<string, Dictionary<int, string>> transcluding,
        IReadOnlyDictionary<string, HashSet<int>> stored, IReadOnlySet<int> pageData)
    {
        var titles = new SortedDictionary<int, string>();
        var missing = new Dictionary<int, List<string>>();
        foreach (var (table, pages) in transcluding)
        {
            foreach (var (id, title) in pages)
            {
                titles[id] = title;
                if (stored[table].Contains(id))
                {
                    continue;
                }

                if (!missing.TryGetValue(id, out var tables))
                {
                    missing[id] = tables = [];
                }

                tables.Add(table);
            }
        }

        return titles
            .Where(t => missing.ContainsKey(t.Key) || !pageData.Contains(t.Key))
            .Select(t => new CargoGap(t.Key, t.Value, missing.GetValueOrDefault(t.Key) ?? [], !pageData.Contains(t.Key)))
            .ToList();
    }

    /// <summary>The gaps that a fresh read still finds.</summary>
    private async Task<List<CargoGap>> RecheckAsync(MySqlConnection conn, List<CargoGap> gaps, CancellationToken ct)
    {
        var ids = gaps.Select(g => g.PageId).ToList();
        var transcluding = new Dictionary<string, Dictionary<int, string>>();
        var stored = new Dictionary<string, HashSet<int>>();
        foreach (var table in gaps.SelectMany(g => g.MissingTables).Distinct())
        {
            transcluding[table] = gaps.Where(g => g.MissingTables.Contains(table)).ToDictionary(g => g.PageId, g => g.Title);
            stored[table] = await StoredAsync(conn, table, ids, ct);
        }

        // Pages missing only _pageData are checked against it alone.
        transcluding["_pageData"] = gaps.ToDictionary(g => g.PageId, g => g.Title);
        stored["_pageData"] = ids.ToHashSet();
        return FindGaps(transcluding, stored, await StoredAsync(conn, "_pageData", ids, ct));
    }

    /// <summary>Cargo tables being rebuilt. While one exists, saves store into it instead of the live table.</summary>
    private async Task<List<string>> RebuildingTablesAsync(MySqlConnection conn, CancellationToken ct) =>
        (await conn.QueryAsync<string>(new CommandDefinition(
            @"SELECT table_name FROM information_schema.tables WHERE table_schema = @schema AND table_name LIKE '%\_\_NEXT'",
            new { schema = _config.CargoDatabase }, cancellationToken: ct))).ToList();

    /// <summary>Main-namespace pages with a row in <paramref name="table"/>, among <paramref name="ids"/> when given.</summary>
    private async Task<HashSet<int>> StoredAsync(MySqlConnection conn, string table, IReadOnlyCollection<int>? ids,
        CancellationToken ct)
    {
        var sql = $"SELECT DISTINCT _pageID FROM `{_config.CargoDatabase}`.`{table}` WHERE _pageNamespace = 0"
                  + (ids == null ? "" : " AND _pageID IN @ids");
        return (await conn.QueryAsync<long>(new CommandDefinition(sql, new { ids }, cancellationToken: ct)))
            .Select(id => (int)id).ToHashSet();
    }

    /// <summary>Main-namespace articles transcluding any of <paramref name="templates"/>, read over the internal API.</summary>
    private async Task<Dictionary<int, string>> TranscludingAsync(string[] templates, CancellationToken ct)
    {
        var pages = new Dictionary<int, string>();
        foreach (var template in templates)
        {
            var query = "action=query&format=json&formatversion=2&list=embeddedin&einamespace=0" +
                        "&eifilterredir=nonredirects&eilimit=max&eititle=" + Uri.EscapeDataString(template);
            await foreach (var json in PagedAsync(query, ct))
            {
                foreach (var page in json["query"]?["embeddedin"]?.AsArray() ?? [])
                {
                    pages[page!["pageid"]!.GetValue<int>()] = page["title"]!.GetValue<string>();
                }
            }
        }

        return pages;
    }

    private sealed record LastEdit(DateTimeOffset Timestamp, string User);

    /// <summary>Each page's latest revision. A page that no longer exists is absent.</summary>
    private async Task<Dictionary<int, LastEdit>> LastEditsAsync(List<int> ids, CancellationToken ct)
    {
        var result = new Dictionary<int, LastEdit>();
        foreach (var chunk in ids.Chunk(50))
        {
            var query = "action=query&format=json&formatversion=2&prop=revisions&rvprop=timestamp|user&pageids=" +
                        string.Join('|', chunk);
            await foreach (var json in PagedAsync(query, ct))
            {
                foreach (var page in json["query"]?["pages"]?.AsArray() ?? [])
                {
                    if (page?["revisions"]?[0] is { } rev)
                    {
                        result[page["pageid"]!.GetValue<int>()] = new LastEdit(
                            rev["timestamp"]!.GetValue<DateTimeOffset>(), rev["user"]?.GetValue<string>() ?? "(hidden)");
                    }
                }
            }
        }

        return result;
    }

    /// <summary>Each response of an API query, following continue.</summary>
    private async IAsyncEnumerable<JsonNode> PagedAsync(string query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(VocabularySource.HttpClientName);
        var next = "&continue=";
        while (next != null)
        {
            await using var stream = await client.GetStreamAsync("w/api.php?" + query + next, ct);
            var json = await JsonSerializer.DeserializeAsync<JsonNode>(stream, cancellationToken: ct)
                       ?? throw new InvalidOperationException("Empty API response");
            if (json["error"] is { } error)
            {
                throw new InvalidOperationException($"API error: {error["info"]}");
            }

            yield return json;
            next = json["continue"] is JsonObject cont
                ? string.Concat(cont.Select(c => $"&{c.Key}={Uri.EscapeDataString(c.Value!.ToString())}"))
                : null;
        }
    }
}
