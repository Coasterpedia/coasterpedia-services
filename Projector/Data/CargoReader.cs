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
    double? Lon)
{
    public StatsRow Stats { get; init; } = StatsRow.Empty;
}

/// <summary>
/// The stat columns, raw. Null where the value is blank or the table has no
/// such column. Text columns stay text: the mapper decides what parses.
/// </summary>
public sealed record StatsRow
{
    public static readonly StatsRow Empty = new();

    public string? Units { get; init; }
    public double? Height { get; init; }
    public double? HeightMetric { get; init; }
    public double? Drop { get; init; }
    public double? DropMetric { get; init; }
    public double? Elevation { get; init; }
    public double? ElevationMetric { get; init; }
    public double? Length { get; init; }
    public double? LengthMetric { get; init; }
    public double? Speed { get; init; }
    public double? SpeedMetric { get; init; }
    public double? Diameter { get; init; }
    public int? TrackInversions { get; init; }
    public int? RiderInversions { get; init; }
    public double? Angle { get; init; }
    public double? BankAngle { get; init; }
    public double? GForce { get; init; }
    public string? Duration { get; init; }
    public string? RidersHour { get; init; }
    public int? RidersTrain { get; init; }
    public int? Tracks { get; init; }
    public double? Dimension1 { get; init; }
    public double? Dimension2 { get; init; }
}

/// <summary>What the wiki's page table says about an ID that has no attraction row.</summary>
public sealed record PageState(int PageId, int Namespace, bool IsRedirect, int? RedirectTargetId);

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

    /// <summary>
    /// Stat alias → column, per table. A table without a column reads NULL, so
    /// the row has one shape and the type's own fields are the only ones set.
    /// Rides.Diameter has no _Metric until the rebuild (A8); the mapper converts it.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> StatColumns = new()
    {
        ["Roller_Coasters"] = new()
        {
            ["units"] = "Units", ["height"] = "Height", ["height_m"] = "Height_Metric",
            ["drop"] = "Tallest_Drop", ["drop_m"] = "Tallest_Drop_Metric",
            ["elevation"] = "Diff_Elevation", ["elevation_m"] = "Diff_Elevation_Metric",
            ["length"] = "Track_Length", ["length_m"] = "Track_Length_Metric",
            ["speed"] = "Speed", ["speed_m"] = "Speed_Metric",
            ["track_inversions"] = "Track_Inversions", ["rider_inversions"] = "Rider_Inversions",
            ["angle"] = "Angle", ["bank_angle"] = "Bank_Angle", ["gforce"] = "GForce",
            ["duration"] = "Duration", ["riders_hour"] = "Riders_Hour", ["riders_train"] = "Riders_Train",
            ["tracks"] = "Tracks", ["dimension1"] = "Dimension1", ["dimension2"] = "Dimension2",
        },
        ["Rides"] = new()
        {
            ["units"] = "Units", ["height"] = "Height", ["height_m"] = "Height_Metric",
            ["drop"] = "Drop_Height", ["drop_m"] = "Drop_Height_Metric",
            ["elevation"] = "Diff_Elevation", ["elevation_m"] = "Diff_Elevation_Metric",
            ["length"] = "Track_Length", ["length_m"] = "Track_Length_Metric",
            ["speed"] = "Speed", ["speed_m"] = "Speed_Metric",
            ["diameter"] = "Diameter", ["gforce"] = "GForce", ["riders_hour"] = "Riders_Hour",
            ["dimension1"] = "Dimension1", ["dimension2"] = "Dimension2",
        },
        ["Water_Slides"] = new()
        {
            ["units"] = "Units", ["height"] = "Height", ["height_m"] = "Height_Metric",
            ["drop"] = "Drop_Height", ["drop_m"] = "Drop_Height_Metric",
            ["elevation"] = "Diff_Elevation", ["elevation_m"] = "Diff_Elevation_Metric",
            ["length"] = "Length", ["length_m"] = "Length_Metric", ["riders_hour"] = "Riders_Hour",
            ["dimension1"] = "Dimension1", ["dimension2"] = "Dimension2",
        },
    };

    private static readonly string[] StatAliases =
    [
        "units", "height", "height_m", "drop", "drop_m", "elevation", "elevation_m", "length", "length_m",
        "speed", "speed_m", "diameter", "track_inversions", "rider_inversions", "angle", "bank_angle", "gforce",
        "duration", "riders_hour", "riders_train", "tracks", "dimension1", "dimension2",
    ];

    // Text and integer columns alike come back as text, so riders_hour is one type
    // whether the table declares it Integer (coasters) or text (rides, slides).
    private static readonly HashSet<string> TextAliases = ["units", "duration", "riders_hour"];

    private static string StatSelect(string table) => string.Join(",\n       ", StatAliases.Select(alias =>
        StatColumns[table].TryGetValue(alias, out var column)
            ? TextAliases.Contains(alias) ? $"CAST(t.`{column}` AS CHAR) AS `{alias}`" : $"t.`{column}` AS `{alias}`"
            : $"NULL AS `{alias}`"));

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
                           t.Name, t.Status, t.Country, t.Manufacturer, t.Coord__lat, t.Coord__lon,
                           {StatSelect(table)}
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
                        Dbl(reader, 7), Dbl(reader, 8))
                    {
                        Stats = ReadStats(reader),
                    });
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
                SELECT p.page_id, p.page_namespace, p.page_is_redirect, d._pageIDOrRedirect
                FROM {Page} p
                LEFT JOIN {Cargo("_pageData")} d ON d._pageID = p.page_id
                WHERE 1 = 1 {InClause(cmd, "p.page_id", chunk)}
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt32(0);
                var ns = Convert.ToInt32(reader.GetValue(1));
                var isRedirect = Convert.ToInt32(reader.GetValue(2)) != 0;
                int? target = reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3));
                result[id] = new PageState(id, ns, isRedirect, isRedirect && target != id ? target : null);
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

    private static StatsRow ReadStats(MySqlDataReader r)
    {
        string? S(string alias) => Str(r, r.GetOrdinal(alias)) is { } v && v.Trim().Length > 0 ? v.Trim() : null;
        double? D(string alias) => Dbl(r, r.GetOrdinal(alias));
        int? I(string alias) => r.IsDBNull(r.GetOrdinal(alias)) ? null : Convert.ToInt32(r.GetValue(r.GetOrdinal(alias)));
        return new StatsRow
        {
            Units = S("units"),
            Height = D("height"), HeightMetric = D("height_m"),
            Drop = D("drop"), DropMetric = D("drop_m"),
            Elevation = D("elevation"), ElevationMetric = D("elevation_m"),
            Length = D("length"), LengthMetric = D("length_m"),
            Speed = D("speed"), SpeedMetric = D("speed_m"),
            Diameter = D("diameter"),
            TrackInversions = I("track_inversions"), RiderInversions = I("rider_inversions"),
            Angle = D("angle"), BankAngle = D("bank_angle"), GForce = D("gforce"),
            Duration = S("duration"), RidersHour = S("riders_hour"),
            RidersTrain = I("riders_train"), Tracks = I("tracks"),
            Dimension1 = D("dimension1"), Dimension2 = D("dimension2"),
        };
    }

    private static string? Str(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static double? Dbl(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i));
}
