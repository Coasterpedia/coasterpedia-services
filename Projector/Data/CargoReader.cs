using System.Data;
using System.Text.RegularExpressions;
using CoasterpediaServices.Projector.Options;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace CoasterpediaServices.Projector.Data;

/// <summary>One attraction's stored row, as the slice reads it.</summary>
public sealed record AttractionRow(
    string Type,
    int PageId,
    string PageName,
    long Revision,
    string? Name,
    string? Status,
    string? Country,
    string? Manufacturer,
    double? Lat,
    double? Lon);

/// <summary>What the wiki's page table says about an ID that has no attraction row.</summary>
public sealed record PageState(int PageId, bool IsRedirect, int? RedirectTargetId);

/// <summary>
/// The Projector's only database code. Raw SQL against the Cargo database, so
/// a missing column fails loudly (unlike a Cargo query, which dies silently)
/// and the job stops with the edge still serving the last good data
/// (DR-010 §3.1). Read-only: the user has no other grant.
/// </summary>
public sealed partial class CargoReader
{
    /// <summary>Attraction type → Cargo table. One row per page, and no page is in two (checked 2026-10-11).</summary>
    public static readonly IReadOnlyList<(string Type, string Table)> AttractionTables =
    [
        ("coaster", "Roller_Coasters"),
        ("ride", "Rides"),
        ("water_slide", "Water_Slides"),
    ];

    private const int InChunk = 1000;

    private readonly ProjectorConfig _config;

    public CargoReader(IOptions<ProjectorConfig> config)
    {
        _config = config.Value;
        foreach (var id in new[] { _config.CargoDatabase, _config.WikiDatabase, _config.WikiTablePrefix })
        {
            if (!IdentifierPattern().IsMatch(id))
            {
                throw new InvalidOperationException($"Not a plain SQL identifier: {id}");
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    private string Cargo(string table) => $"`{_config.CargoDatabase}`.`{table}`";
    private string Page => $"`{_config.WikiDatabase}`.`{_config.WikiTablePrefix}page`";

    public async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new MySqlConnection(_config.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>
    /// Every attraction row, or only those of <paramref name="pageIds"/>. Read
    /// inside one REPEATABLE READ transaction so the three tables agree.
    /// </summary>
    public async Task<List<AttractionRow>> ReadAttractionsAsync(MySqlConnection conn, IReadOnlyCollection<int>? pageIds,
        CancellationToken ct)
    {
        var rows = new List<AttractionRow>();
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        foreach (var (type, table) in AttractionTables)
        {
            foreach (var chunk in Chunks(pageIds))
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"""
                    SELECT t._pageID, t._pageName, p.page_latest,
                           t.Name, t.Status, t.Country, t.Manufacturer, t.Coord__lat, t.Coord__lon
                    FROM {Cargo(table)} t
                    JOIN {Page} p ON p.page_id = t._pageID
                    WHERE t._pageNamespace = 0 {InClause(cmd, "t._pageID", chunk)}
                    """;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    rows.Add(new AttractionRow(
                        type,
                        reader.GetInt32(0),
                        reader.GetString(1),
                        Convert.ToInt64(reader.GetValue(2)),
                        Str(reader, 3), Str(reader, 4), Str(reader, 5), Str(reader, 6),
                        Dbl(reader, 7), Dbl(reader, 8)));
                }
            }
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>
    /// Main-namespace titles → the page ID they answer to, following one
    /// redirect (Cargo's _pageData). A title with no page is absent: a red link.
    /// </summary>
    public async Task<Dictionary<string, int>> ResolveTitlesAsync(MySqlConnection conn, IReadOnlyCollection<string> titles,
        CancellationToken ct)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var chunk in titles.Distinct().Chunk(InChunk))
        {
            await using var cmd = conn.CreateCommand();
            var names = string.Join(',', chunk.Select((t, i) =>
            {
                cmd.Parameters.AddWithValue($"@t{i}", t);
                return $"@t{i}";
            }));
            cmd.CommandText = $"""
                SELECT _pageName, _pageIDOrRedirect FROM {Cargo("_pageData")}
                WHERE _pageNamespace = 0 AND _pageName IN ({names})
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // The column's collation may be case-blind; titles are not.
                var name = reader.GetString(0);
                if (!reader.IsDBNull(1) && chunk.Contains(name, StringComparer.Ordinal))
                {
                    result[name] = Convert.ToInt32(reader.GetValue(1));
                }
            }
        }

        return result;
    }

    /// <summary>The page table's view of IDs that produced no attraction row. An ID absent from the result no longer exists.</summary>
    public async Task<Dictionary<int, PageState>> ReadPageStatesAsync(MySqlConnection conn, IReadOnlyCollection<int> pageIds,
        CancellationToken ct)
    {
        var result = new Dictionary<int, PageState>();
        foreach (var chunk in Chunks(pageIds))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT p.page_id, p.page_is_redirect, d._pageIDOrRedirect
                FROM {Page} p
                LEFT JOIN {Cargo("_pageData")} d ON d._pageID = p.page_id
                WHERE 1 = 1 {InClause(cmd, "p.page_id", chunk)}
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt32(0);
                var isRedirect = Convert.ToInt32(reader.GetValue(1)) != 0;
                int? target = reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2));
                result[id] = new PageState(id, isRedirect, isRedirect && target != id ? target : null);
            }
        }

        return result;
    }

    private static IEnumerable<int[]?> Chunks(IReadOnlyCollection<int>? ids) =>
        ids == null ? new int[]?[] { null } : ids.Distinct().Chunk(InChunk).Cast<int[]?>();

    private static string InClause(MySqlCommand cmd, string column, int[]? ids)
    {
        if (ids == null)
        {
            return "";
        }

        if (ids.Length == 0)
        {
            return "AND 1 = 0";
        }

        var names = ids.Select((id, i) =>
        {
            cmd.Parameters.AddWithValue($"@id{i}", id);
            return $"@id{i}";
        });
        return $"AND {column} IN ({string.Join(',', names)})";
    }

    private static string? Str(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static double? Dbl(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i));
}
