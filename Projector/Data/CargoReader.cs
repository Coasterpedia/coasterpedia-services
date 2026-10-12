using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;
using CoasterpediaServices.Projector.Options;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace CoasterpediaServices.Projector.Data;

/// <summary>One attraction's stored row, as the slice reads it.</summary>
public sealed record AttractionRow
{
    public required string Type { get; init; }
    public required int PageId { get; init; }
    public required string PageName { get; init; }
    public required long Revision { get; init; }
    public string? Name { get; init; }
    public string? Status { get; init; }
    public string? Country { get; init; }
    public string? Manufacturer { get; init; }
    public double? Lat { get; init; }
    public double? Lon { get; init; }
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
    public double? DiameterMetric { get; init; }
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

/// <summary>
/// One park's stored row. Dates are text (yyyy-mm-dd), so a zero or odd date
/// reaches the mapper as written instead of failing the read.
/// </summary>
public sealed record ParkRow
{
    public required int PageId { get; init; }
    public required string PageName { get; init; }
    public required long Revision { get; init; }
    public string? Name { get; init; }
    public string? Status { get; init; }
    public string? Country { get; init; }
    public string? State { get; init; }
    public string? Location { get; init; }
    public string? Website { get; init; }
    public double? Lat { get; init; }
    public double? Lon { get; init; }

    public string? OpenedEdtf { get; init; }
    public string? OpenedEarliest { get; init; }
    public string? OpenedLatest { get; init; }
    public string? ClosedEdtf { get; init; }
    public string? ClosedEarliest { get; init; }
    public string? ClosedLatest { get; init; }

    /// <summary>Opening has no EDTF columns (not part of A9): a Date and Cargo's precision for it.</summary>
    public string? Opening { get; init; }
    public int? OpeningPrecision { get; init; }
}

/// <summary>One manufacturer's stored row. Founded is a Date (yyyy-mm-dd) and Cargo's precision for it; there are no EDTF columns.</summary>
public sealed record ManufacturerRow
{
    public required int PageId { get; init; }
    public required string PageName { get; init; }
    public required long Revision { get; init; }
    public string? Name { get; init; }
    public string? Status { get; init; }
    public string? Country { get; init; }
    public string? Headquarters { get; init; }
    public string? Website { get; init; }
    public string? Founded { get; init; }
    public int? FoundedPrecision { get; init; }
}

/// <summary>
/// One product's stored row. The capability columns are Cargo list fields,
/// read as stored (<c>__full</c>, '#'-separated): the mapper splits them.
/// Units is the page's, and also the synthesised variant row's, which stores none.
/// </summary>
public sealed record ProductRow
{
    public required string Type { get; init; }
    public required int PageId { get; init; }
    public required string PageName { get; init; }
    public required long Revision { get; init; }
    public string? Name { get; init; }
    public string? Status { get; init; }
    public string? Manufacturer { get; init; }
    public string? Units { get; init; }

    public string? Introduced { get; init; }
    public int? IntroducedPrecision { get; init; }
    public string? Discontinued { get; init; }
    public int? DiscontinuedPrecision { get; init; }

    public string? TrackMaterial { get; init; }
    public string? SupportMaterial { get; init; }
    public string? Restraints { get; init; }
    public string? Classes { get; init; }
    public string? Containment { get; init; }
    public string? SlideMaterial { get; init; }
    public string? RidersTube { get; init; }

    /// <summary>The page's variant rows in the order they were stored: the infobox's own (unnamed) first, then each block.</summary>
    public IReadOnlyList<VariantRow> Variants { get; init; } = [];
}

/// <summary>One <c>&lt;Class&gt;_Product_Variants</c> row. Stats.Units is the row's own, null on the synthesised row.</summary>
public sealed record VariantRow
{
    public required int PageId { get; init; }
    public required long RowId { get; init; }
    public string? Name { get; init; }
    public string? Group { get; init; }
    public string? Introduced { get; init; }
    public int? IntroducedPrecision { get; init; }
    public string? Discontinued { get; init; }
    public int? DiscontinuedPrecision { get; init; }
    public StatsRow Stats { get; init; } = StatsRow.Empty;
    public double? Sweep { get; init; }
    public int? Cabins { get; init; }
    public int? NumberOfSlides { get; init; }
    public double? MaxWeight { get; init; }
    public double? MaxWeightPerTube { get; init; }
}

/// <summary>
/// One park's membership of a resort, from the Resorts table. The rows are
/// stored by the resort's wrapper template (Template:Alton Towers Resort), so
/// the template's page is the resort's identity; Park is the member's title.
/// </summary>
public sealed record ResortMemberRow
{
    public required int ResortId { get; init; }
    public string? Resort { get; init; }
    public string? Park { get; init; }
    public int? Ordering { get; init; }
}

/// <summary>What the wiki's page table says about an ID that has no row of the kind being read.</summary>
public sealed record PageState
{
    public required int PageId { get; init; }
    public required int Namespace { get; init; }
    public required bool IsRedirect { get; init; }
    public int? RedirectTargetId { get; init; }
}

/// <summary>
/// The Projector's only database code. Raw SQL against the Cargo database, so
/// a missing column fails loudly (unlike a Cargo query, which dies silently)
/// and the job stops with the edge still serving the last good data
/// (DR-010 §3.1). Read-only: the user has no other grant.
/// Rows are filled by column name: every column is aliased to its property.
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
    /// StatsRow property → column, per table. A table without a column reads NULL,
    /// so the row has one shape and the type's own fields are the only ones set.
    /// Rides.Diameter has no _Metric until the rebuild (A8); the mapper converts it.
    /// </summary>
    internal static readonly Dictionary<string, Dictionary<string, string>> StatColumns = new()
    {
        ["Roller_Coasters"] = new()
        {
            ["Units"] = "Units", ["Height"] = "Height", ["HeightMetric"] = "Height_Metric",
            ["Drop"] = "Tallest_Drop", ["DropMetric"] = "Tallest_Drop_Metric",
            ["Elevation"] = "Diff_Elevation", ["ElevationMetric"] = "Diff_Elevation_Metric",
            ["Length"] = "Track_Length", ["LengthMetric"] = "Track_Length_Metric",
            ["Speed"] = "Speed", ["SpeedMetric"] = "Speed_Metric",
            ["TrackInversions"] = "Track_Inversions", ["RiderInversions"] = "Rider_Inversions",
            ["Angle"] = "Angle", ["BankAngle"] = "Bank_Angle", ["GForce"] = "GForce",
            ["Duration"] = "Duration", ["RidersHour"] = "Riders_Hour", ["RidersTrain"] = "Riders_Train",
            ["Tracks"] = "Tracks", ["Dimension1"] = "Dimension1", ["Dimension2"] = "Dimension2",
        },
        ["Rides"] = new()
        {
            ["Units"] = "Units", ["Height"] = "Height", ["HeightMetric"] = "Height_Metric",
            ["Drop"] = "Drop_Height", ["DropMetric"] = "Drop_Height_Metric",
            ["Elevation"] = "Diff_Elevation", ["ElevationMetric"] = "Diff_Elevation_Metric",
            ["Length"] = "Track_Length", ["LengthMetric"] = "Track_Length_Metric",
            ["Speed"] = "Speed", ["SpeedMetric"] = "Speed_Metric",
            ["Diameter"] = "Diameter", ["GForce"] = "GForce", ["RidersHour"] = "Riders_Hour",
            ["Dimension1"] = "Dimension1", ["Dimension2"] = "Dimension2",
        },
        ["Water_Slides"] = new()
        {
            ["Units"] = "Units", ["Height"] = "Height", ["HeightMetric"] = "Height_Metric",
            ["Drop"] = "Drop_Height", ["DropMetric"] = "Drop_Height_Metric",
            ["Elevation"] = "Diff_Elevation", ["ElevationMetric"] = "Diff_Elevation_Metric",
            ["Length"] = "Length", ["LengthMetric"] = "Length_Metric", ["RidersHour"] = "Riders_Hour",
            ["Dimension1"] = "Dimension1", ["Dimension2"] = "Dimension2",
        },
    };

    /// <summary>
    /// Product type → its product table, its variant table, and the capability
    /// list fields it declares (ProductRow property → column). One row per page in
    /// each, and no page in two (checked 2026-10-12: 257 + 713 + 122).
    /// </summary>
    internal static readonly IReadOnlyList<(string Type, string Table, string Variants, Dictionary<string, string> Lists)> ProductTables =
    [
        ("coaster", "Roller_Coaster_Products", "Roller_Coaster_Product_Variants",
            new() { ["TrackMaterial"] = "Track_Material", ["SupportMaterial"] = "Support_Material", ["Restraints"] = "Restraints" }),
        ("ride", "Ride_Products", "Ride_Product_Variants",
            new() { ["Restraints"] = "Restraints" }),
        ("water_slide", "Water_Slide_Products", "Water_Slide_Product_Variants",
            new()
            {
                ["Classes"] = "Class", ["Containment"] = "Containment", ["SlideMaterial"] = "Slide_Material",
                ["RidersTube"] = "Riders_Tube",
            }),
    ];

    internal static readonly string[] ListProperties =
        ["TrackMaterial", "SupportMaterial", "Restraints", "Classes", "Containment", "SlideMaterial", "RidersTube"];

    /// <summary>
    /// The variant tables' StatsRow columns, plus their own (VariantSpecs). Coaster
    /// variants name the drop Drop_Height, not Tallest_Drop; slide variants'
    /// Diff_Elevation has no _Metric, so the mapper converts it.
    /// </summary>
    internal static readonly Dictionary<string, Dictionary<string, string>> VariantColumns = new()
    {
        ["Roller_Coaster_Product_Variants"] = new()
        {
            ["Units"] = "Units", ["Height"] = "Height", ["HeightMetric"] = "Height_Metric",
            ["Drop"] = "Drop_Height", ["DropMetric"] = "Drop_Height_Metric",
            ["Length"] = "Track_Length", ["LengthMetric"] = "Track_Length_Metric",
            ["Speed"] = "Speed", ["SpeedMetric"] = "Speed_Metric",
            ["TrackInversions"] = "Track_Inversions", ["RiderInversions"] = "Rider_Inversions",
            ["Angle"] = "Angle", ["BankAngle"] = "Bank_Angle", ["GForce"] = "GForce",
            ["Duration"] = "Duration", ["RidersHour"] = "Riders_Hour", ["RidersTrain"] = "Riders_Train",
            ["Dimension1"] = "Dimension1", ["Dimension2"] = "Dimension2",
        },
        ["Ride_Product_Variants"] = new()
        {
            ["Units"] = "Units", ["Height"] = "Height", ["HeightMetric"] = "Height_Metric",
            ["Speed"] = "Speed", ["SpeedMetric"] = "Speed_Metric",
            ["Diameter"] = "Diameter", ["DiameterMetric"] = "Diameter_Metric",
            ["Duration"] = "Duration", ["RidersHour"] = "Riders_Hour", ["RidersTrain"] = "Riders_Train",
            ["Dimension1"] = "Dimension1", ["Dimension2"] = "Dimension2",
            ["Cabins"] = "Cabins", ["Sweep"] = "Sweep",
        },
        ["Water_Slide_Product_Variants"] = new()
        {
            ["Units"] = "Units", ["Height"] = "Height", ["HeightMetric"] = "Height_Metric",
            ["Drop"] = "Drop_Height", ["DropMetric"] = "Drop_Height_Metric",
            ["Elevation"] = "Diff_Elevation",
            ["Length"] = "Length", ["LengthMetric"] = "Length_Metric",
            ["Angle"] = "Angle", ["Duration"] = "Duration", ["RidersHour"] = "Riders_Hour",
            ["Dimension1"] = "Dimension1", ["Dimension2"] = "Dimension2",
            ["NumberOfSlides"] = "Number_Of_Slides", ["MaxWeight"] = "Max_Weight",
            ["MaxWeightPerTube"] = "Max_Weight_Per_Tube",
        },
    };

    /// <summary>The VariantRow properties that only some variant tables have.</summary>
    internal static readonly string[] VariantSpecs = ["Sweep", "Cabins", "NumberOfSlides", "MaxWeight", "MaxWeightPerTube"];

    private static readonly PropertyInfo[] StatProperties = ColumnProperties(typeof(StatsRow));

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

    // page_latest is unsigned, and Dapper wraps an unsigned value past 2^31 when it widens it to long.
    private const string Revision = "CAST(p.page_latest AS SIGNED) AS Revision";

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
            foreach (var ids in Chunks(pageIds))
            {
                rows.AddRange(await QueryAsync<AttractionRow>(conn, tx, $"""
                    SELECT @type AS Type, t._pageID AS PageId, t._pageName AS PageName, {Revision},
                           t.Name, t.Status, t.Country, t.Manufacturer, t.Coord__lat AS Lat, t.Coord__lon AS Lon,
                           {Select(StatProperties, StatColumns[table])}
                    FROM {Cargo(table)} t
                    JOIN {Page} p ON p.page_id = t._pageID
                    WHERE t._pageNamespace = 0 {PageFilter("t._pageID", ids)}
                    """, new { type, ids }, ct, (row, stats) => row with { Stats = stats }));
            }
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>
    /// Every park row, or only those of <paramref name="pageIds"/>. Amusement_Parks
    /// is rebuilt with A7 and A9 (Status as a String, the EDTF date columns), so
    /// this read fails loudly against a table that is not.
    /// </summary>
    public async Task<List<ParkRow>> ReadParksAsync(MySqlConnection conn, IReadOnlyCollection<int>? pageIds, CancellationToken ct)
    {
        var rows = new List<ParkRow>();
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        foreach (var ids in Chunks(pageIds))
        {
            rows.AddRange(await QueryAsync<ParkRow>(conn, tx, $"""
                SELECT t._pageID AS PageId, t._pageName AS PageName, {Revision},
                       t.Name, t.Status, t.Country, t.State, t.Location, t.Website,
                       t.Coordinates__lat AS Lat, t.Coordinates__lon AS Lon,
                       t.Opened_EDTF AS OpenedEdtf,
                       CAST(t.Opened_Earliest AS CHAR) AS OpenedEarliest, CAST(t.Opened_Latest AS CHAR) AS OpenedLatest,
                       t.Closed_EDTF AS ClosedEdtf,
                       CAST(t.Closed_Earliest AS CHAR) AS ClosedEarliest, CAST(t.Closed_Latest AS CHAR) AS ClosedLatest,
                       CAST(t.Opening AS CHAR) AS Opening, t.Opening__precision AS OpeningPrecision
                FROM {Cargo("Amusement_Parks")} t
                JOIN {Page} p ON p.page_id = t._pageID
                WHERE t._pageNamespace = 0 {PageFilter("t._pageID", ids)}
                """, new { ids }, ct));
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>Every manufacturer row, or only those of <paramref name="pageIds"/>.</summary>
    public async Task<List<ManufacturerRow>> ReadManufacturersAsync(MySqlConnection conn, IReadOnlyCollection<int>? pageIds,
        CancellationToken ct)
    {
        var rows = new List<ManufacturerRow>();
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        foreach (var ids in Chunks(pageIds))
        {
            rows.AddRange(await QueryAsync<ManufacturerRow>(conn, tx, $"""
                SELECT t._pageID AS PageId, t._pageName AS PageName, {Revision},
                       t.Name, t.Status, t.Country, t.Headquarters, t.Website,
                       CAST(t.Founded AS CHAR) AS Founded, t.Founded__precision AS FoundedPrecision
                FROM {Cargo("Manufacturers")} t
                JOIN {Page} p ON p.page_id = t._pageID
                WHERE t._pageNamespace = 0 {PageFilter("t._pageID", ids)}
                ORDER BY t._pageID, t._ID
                """, new { ids }, ct));
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>
    /// Every product row, or only those of <paramref name="pageIds"/>, each with
    /// its variant rows. One transaction, so a product and its variants agree.
    /// </summary>
    public async Task<List<ProductRow>> ReadProductsAsync(MySqlConnection conn, IReadOnlyCollection<int>? pageIds,
        CancellationToken ct)
    {
        var rows = new List<ProductRow>();
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        foreach (var (type, table, variantTable, lists) in ProductTables)
        {
            var variants = new List<VariantRow>();
            foreach (var ids in Chunks(pageIds))
            {
                variants.AddRange(await QueryAsync<VariantRow>(conn, tx, $"""
                    SELECT t._pageID AS PageId, t._ID AS RowId, t.Variant_Name AS Name, t.Variant_Group AS `Group`,
                           CAST(t.Introduced AS CHAR) AS Introduced, t.Introduced__precision AS IntroducedPrecision,
                           CAST(t.Discontinued AS CHAR) AS Discontinued, t.Discontinued__precision AS DiscontinuedPrecision,
                           {Select(ColumnProperties(typeof(VariantRow), VariantSpecs), VariantColumns[variantTable])},
                           {Select(StatProperties, VariantColumns[variantTable])}
                    FROM {Cargo(variantTable)} t
                    WHERE t._pageNamespace = 0 {PageFilter("t._pageID", ids)}
                    ORDER BY t._pageID, t._ID
                    """, new { ids }, ct, (row, stats) => row with { Stats = stats }));
            }

            var byPage = variants.ToLookup(v => v.PageId);
            var listSelect = string.Join(",\n       ", ListProperties.Select(property =>
                lists.TryGetValue(property, out var column)
                    ? $"CAST(t.`{column}__full` AS CHAR) AS `{property}`"
                    : $"NULL AS `{property}`"));
            foreach (var ids in Chunks(pageIds))
            {
                var products = await QueryAsync<ProductRow>(conn, tx, $"""
                    SELECT @type AS Type, t._pageID AS PageId, t._pageName AS PageName, {Revision},
                           t.Name, t.Status, t.Manufacturer, t.Units,
                           CAST(t.Introduced AS CHAR) AS Introduced, t.Introduced__precision AS IntroducedPrecision,
                           CAST(t.Discontinued AS CHAR) AS Discontinued, t.Discontinued__precision AS DiscontinuedPrecision,
                           {listSelect}
                    FROM {Cargo(table)} t
                    JOIN {Page} p ON p.page_id = t._pageID
                    WHERE t._pageNamespace = 0 {PageFilter("t._pageID", ids)}
                    ORDER BY t._pageID, t._ID
                    """, new { type, ids }, ct);
                rows.AddRange(products.Select(p => p with { Variants = [.. byPage[p.PageId]] }));
            }
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>
    /// Every resort membership: about 90 rows, read whole on every pass, since
    /// one is stored by a template rather than by the park it names.
    /// </summary>
    public Task<List<ResortMemberRow>> ReadResortMembersAsync(MySqlConnection conn, CancellationToken ct) =>
        QueryAsync<ResortMemberRow>(conn, null, $"""
            SELECT t._pageID AS ResortId, t.Resort, t.Park, t.Ordering
            FROM {Cargo("Resorts")} t
            WHERE t._pageNamespace = 10
            """, null, ct);

    private sealed record TitleRow
    {
        public required string PageName { get; init; }
        public int? Target { get; init; }
    }

    /// <summary>
    /// Main-namespace titles → the page ID they answer to, following one
    /// redirect (Cargo's _pageData). A title with no page is absent: a red link.
    /// </summary>
    public async Task<Dictionary<string, int>> ResolveTitlesAsync(MySqlConnection conn, IReadOnlyCollection<string> titles,
        CancellationToken ct)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var names in titles.Distinct().Chunk(InChunk))
        {
            var found = await QueryAsync<TitleRow>(conn, null, $"""
                SELECT _pageName AS PageName, _pageIDOrRedirect AS Target FROM {Cargo("_pageData")}
                WHERE _pageNamespace = 0 AND _pageName IN @names
                """, new { names }, ct);
            foreach (var row in found)
            {
                // The column's collation may be case-blind; titles are not.
                if (row.Target is { } id && names.Contains(row.PageName, StringComparer.Ordinal))
                {
                    result[row.PageName] = id;
                }
            }
        }

        return result;
    }

    /// <summary>The page table's view of IDs that produced no row. An ID absent from the result no longer exists.</summary>
    public async Task<Dictionary<int, PageState>> ReadPageStatesAsync(MySqlConnection conn, IReadOnlyCollection<int> pageIds,
        CancellationToken ct)
    {
        var result = new Dictionary<int, PageState>();
        foreach (var ids in Chunks(pageIds))
        {
            var states = await QueryAsync<PageState>(conn, null, $"""
                SELECT p.page_id AS PageId, p.page_namespace AS Namespace, p.page_is_redirect <> 0 AS IsRedirect,
                       CASE WHEN p.page_is_redirect <> 0 AND d._pageIDOrRedirect <> p.page_id
                            THEN d._pageIDOrRedirect END AS RedirectTargetId
                FROM {Page} p
                LEFT JOIN {Cargo("_pageData")} d ON d._pageID = p.page_id
                WHERE 1 = 1 {PageFilter("p.page_id", ids)}
                """, new { ids }, ct);
            foreach (var state in states)
            {
                result[state.PageId] = state;
            }
        }

        return result;
    }

    /// <summary>
    /// Rows of <typeparamref name="T"/>. With <paramref name="withStats"/>, the
    /// columns from the first stat on are a StatsRow, attached to each row.
    /// </summary>
    private static async Task<List<T>> QueryAsync<T>(MySqlConnection conn, MySqlTransaction? tx, string sql, object? param,
        CancellationToken ct, Func<T, StatsRow, T>? withStats = null)
    {
        await using var reader = await conn.ExecuteReaderAsync(new CommandDefinition(sql, param, tx, cancellationToken: ct));
        var split = withStats == null ? reader.FieldCount : reader.GetOrdinal(StatProperties[0].Name);
        var parse = Parser<T>(reader, 0, split);
        var parseStats = withStats == null ? null : Parser<StatsRow>(reader, split, reader.FieldCount);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            var row = parse(reader);
            rows.Add(parseStats == null ? row : withStats!(row, Trimmed(parseStats(reader))));
        }

        return rows;
    }

    /// <summary>
    /// A parser for <typeparamref name="T"/> over columns [start, end). Dapper
    /// skips a column that names no property and leaves a property with no column
    /// at its default, so both are refused here: a misspelt alias stops the pass
    /// instead of reading as null.
    /// </summary>
    private static Func<DbDataReader, T> Parser<T>(DbDataReader reader, int start, int end)
    {
        var columns = Enumerable.Range(start, end - start).Select(reader.GetName).ToList();
        var properties = ColumnProperties(typeof(T)).Select(p => p.Name).ToList();
        var missing = properties.Except(columns, StringComparer.OrdinalIgnoreCase).ToList();
        var unknown = columns.Except(properties, StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0 || unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"{typeof(T).Name}: no column for [{string.Join(", ", missing)}], no property for [{string.Join(", ", unknown)}]");
        }

        return reader.GetRowParser<T>(typeof(T), start, end - start);
    }

    /// <summary>The properties a query fills: scalar ones, optionally only <paramref name="names"/>, in that order.</summary>
    internal static PropertyInfo[] ColumnProperties(Type type, IEnumerable<string>? names = null)
    {
        var scalar = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => (Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType) is var t && (t.IsPrimitive || t == typeof(string)))
            .ToDictionary(p => p.Name);
        return names == null ? [.. scalar.Values] : [.. names.Select(n => scalar[n])];
    }

    /// <summary>
    /// The SELECT list for <paramref name="properties"/>: the table's column where
    /// it has one, else NULL, aliased to the property. Text properties read as text
    /// whatever the column's type, so RidersHour is a string whether the table
    /// declares it Integer (coasters) or text (rides, slides).
    /// </summary>
    private static string Select(IEnumerable<PropertyInfo> properties, IReadOnlyDictionary<string, string> columns) =>
        string.Join(",\n       ", properties.Select(p =>
            !columns.TryGetValue(p.Name, out var column) ? $"NULL AS `{p.Name}`"
            : p.PropertyType == typeof(string) ? $"CAST(t.`{column}` AS CHAR) AS `{p.Name}`"
            : $"t.`{column}` AS `{p.Name}`"));

    private static StatsRow Trimmed(StatsRow s) =>
        s with { Units = Blank(s.Units), Duration = Blank(s.Duration), RidersHour = Blank(s.RidersHour) };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IEnumerable<int[]?> Chunks(IReadOnlyCollection<int>? ids) =>
        ids == null ? new int[]?[] { null } : ids.Distinct().Chunk(InChunk).Cast<int[]?>();

    /// <summary>Dapper expands <c>IN @ids</c> to one parameter per ID; a null chunk reads the whole table.</summary>
    private static string PageFilter(string column, int[]? ids) => ids == null ? "" : $"AND {column} IN @ids";
}
