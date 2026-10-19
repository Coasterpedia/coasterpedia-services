using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;
using CoasterpediaServices.Projector.Options;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace CoasterpediaServices.Projector.Data;

/// <summary>
/// One attraction's stored row, as the slice reads it. Columns only some
/// tables have (AttractionColumns) read NULL elsewhere. List fields are read
/// as stored ('#'-separated); dates as text, as for parks.
/// </summary>
public sealed record AttractionRow
{
    public required string Type { get; init; }
    public required int PageId { get; init; }
    public required string PageName { get; init; }
    public required long Revision { get; init; }
    public string? Name { get; init; }

    /// <summary>1 when the page carries {{Placeholder title}} (its category, from _pageData); 0 otherwise.</summary>
    public long PlaceholderTitle { get; init; }

    public string? Status { get; init; }
    public string? Country { get; init; }
    public string? Manufacturer { get; init; }

    // One-or-many cells, as typed (DR-010 §12.18): every table has them.
    public string? Designer { get; init; }
    public string? Builder { get; init; }
    public string? Replaced { get; init; }
    public string? Replacement { get; init; }

    /// <summary>The site's type tags. Category1 on every table; 2–6 on coasters and slides only (AttractionColumns).</summary>
    public string? Category1 { get; init; }
    public string? Category2 { get; init; }
    public string? Category3 { get; init; }
    public string? Category4 { get; init; }
    public string? Category5 { get; init; }
    public string? Category6 { get; init; }

    /// <summary>Product and Product_Variant are list fields on all three tables (rebuild A3), read as stored ('#'-separated).</summary>
    public string? Product { get; init; }
    public string? ProductVariant { get; init; }

    public double? Lat { get; init; }
    public double? Lon { get; init; }

    // Where it is (DR-006): the stored answer of Travelling.classify, and the columns it read.
    public string? LocationKind { get; init; }
    public bool? OnLoan { get; init; }
    public string? Park { get; init; }
    public string? Owner { get; init; }
    public string? Location { get; init; }
    public string? State { get; init; }
    public string? Section { get; init; }
    public string? Subsection { get; init; }

    public string? OpenedEdtf { get; init; }
    public string? OpenedEarliest { get; init; }
    public string? OpenedLatest { get; init; }
    public string? ClosedEdtf { get; init; }
    public string? ClosedEarliest { get; init; }
    public string? ClosedLatest { get; init; }
    public string? Opening { get; init; }
    public int? OpeningPrecision { get; init; }
    public string? Closing { get; init; }
    public int? ClosingPrecision { get; init; }

    public string? Restraints { get; init; }

    // Coasters.
    public string? Propulsion { get; init; }
    public string? TrackMaterial { get; init; }
    public string? SupportMaterial { get; init; }
    public string? TrainManufacturer { get; init; }

    // Rides.
    public bool? Indoor { get; init; }
    public bool? Enclosed { get; init; }
    public bool? DarkRide { get; init; }
    public string? Cabins { get; init; }
    public int? NumberOfRides { get; init; }
    public double? Coord2Lat { get; init; }
    public double? Coord2Lon { get; init; }
    public double? Coord3Lat { get; init; }
    public double? Coord3Lon { get; init; }
    public double? Coord4Lat { get; init; }
    public double? Coord4Lon { get; init; }
    public double? Coord5Lat { get; init; }
    public double? Coord5Lon { get; init; }
    public double? Coord6Lat { get; init; }
    public double? Coord6Lon { get; init; }

    // Water slides.
    public string? SlideClasses { get; init; }
    public string? Environment { get; init; }
    public string? Containment { get; init; }
    public string? ExitType { get; init; }
    public int? NumberOfSlides { get; init; }

    // Rider requirements, in the page's Units.
    public double? MinHeight { get; init; }
    public double? MinHeightDriver { get; init; }
    public double? MinHeightStanding { get; init; }
    public double? MinHeightUpsideDown { get; init; }
    public double? MinHeightUnaccompanied { get; init; }
    public double? MaxHeightWithoutChild { get; init; }
    public double? MaxHeight { get; init; }
    public int? MinAge { get; init; }
    public int? MinAgeDriver { get; init; }
    public int? MinAgeUnaccompanied { get; init; }
    public int? MaxAgeWithoutChild { get; init; }
    public int? MaxAge { get; init; }
    public bool? ChildrenOnly { get; init; }
    public bool? AdultOnlyWithChild { get; init; }
    public double? MinWeight { get; init; }
    public double? MaxWeight { get; init; }
    public double? MaxWeightPerTube { get; init; }

    /// <summary>YouTube video IDs, as the infobox validates them: Video, Video2, Video3 on every table.</summary>
    public string? Video { get; init; }
    public string? Video2 { get; init; }
    public string? Video3 { get; init; }

    public StatsRow Stats { get; init; } = StatsRow.Empty;

    /// <summary>Roller_Coaster_Past_Locations rows on the page, newest stint first (Ordering 1).</summary>
    public IReadOnlyList<PastLocationRow> PastLocations { get; init; } = [];

    /// <summary>Coaster_Elements.Element: page titles in layout order, repeats kept (two vertical loops are two).</summary>
    public IReadOnlyList<string> Elements { get; init; } = [];

    /// <summary>Water_Slide_Components rows on the page, by Ordering.</summary>
    public IReadOnlyList<ComponentRow> Components { get; init; } = [];

    /// <summary>The page's name History rows (project 3): one set per location, LocationOrdering 0 the current one.</summary>
    public IReadOnlyList<HistoryRow> Names { get; init; } = [];

    /// <summary>Coaster_Tracks rows, by Ordering: only a multitrack coaster with two or more tracks has any.</summary>
    public IReadOnlyList<TrackRow> TrackRows { get; init; } = [];
}

/// <summary>One track of a multitrack coaster, with its own figures in the columns Roller_Coasters uses.</summary>
public sealed record TrackRow
{
    public required int PageId { get; init; }
    public int? Ordering { get; init; }
    public string? Name { get; init; }
    public StatsRow Stats { get; init; } = StatsRow.Empty;
}

/// <summary>One past stint. Every attraction type stores here, despite the table's name.</summary>
public sealed record PastLocationRow
{
    public required int PageId { get; init; }
    public int? Ordering { get; init; }
    public string? LocationKind { get; init; }
    public bool? OnLoan { get; init; }
    public string? Park { get; init; }
    public string? Owner { get; init; }
    public string? Location { get; init; }
    public string? State { get; init; }
    public string? Country { get; init; }
    public double? Lat { get; init; }
    public double? Lon { get; init; }
    public string? OpenedEdtf { get; init; }
    public string? OpenedEarliest { get; init; }
    public string? OpenedLatest { get; init; }
    public string? ClosedEdtf { get; init; }
    public string? ClosedEarliest { get; init; }
    public string? ClosedLatest { get; init; }
}

/// <summary>One Water_Slide_Components row: a slot's product (wikitext) and variant.</summary>
public sealed record ComponentRow
{
    public required int PageId { get; init; }
    public int? Ordering { get; init; }
    public string? Product { get; init; }
    public string? Variant { get; init; }
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

    /// <summary>The page's name, owner and operator History rows, in page order (Position 1 is the one in force).</summary>
    public IReadOnlyList<HistoryRow> History { get; init; } = [];
}

/// <summary>
/// One History row: an owner or operator spell on a park page, or a name on a
/// park or an attraction. Name is as typed: a link, several, or plain text.
/// Start and End are Dates at Cargo's precision; History has no EDTF columns.
/// LocationOrdering is set only on an attraction's rows: 0 for the current
/// location, else the past stint's Ordering.
/// </summary>
public sealed record HistoryRow
{
    public required int PageId { get; init; }
    public required string Type { get; init; }
    public string? Name { get; init; }
    public string? Start { get; init; }
    public int? StartPrecision { get; init; }
    public string? End { get; init; }
    public int? EndPrecision { get; init; }
    public int? Position { get; init; }
    public int? LocationOrdering { get; init; }
}

/// <summary>One park company's stored row (Park_Companies). Established and Closed are Dates at Cargo's precision.</summary>
public sealed record ParkCompanyRow
{
    public required int PageId { get; init; }
    public required string PageName { get; init; }
    public required long Revision { get; init; }
    public string? Name { get; init; }
    public string? Status { get; init; }
    public string? Country { get; init; }
    public string? Headquarters { get; init; }
    public string? Website { get; init; }
    public string? Established { get; init; }
    public int? EstablishedPrecision { get; init; }
    public string? Closed { get; init; }
    public int? ClosedPrecision { get; init; }
    public string? Predecessor { get; init; }
    public string? Successor { get; init; }
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
    public string? Predecessor { get; init; }
    public string? Successor { get; init; }
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
    public string? Designer { get; init; }
    public string? Units { get; init; }

    /// <summary>Ride products' Type: one wikitext cell, where the other two tables keep a list (Types).</summary>
    public string? TypeText { get; init; }

    public string? IntroducedEdtf { get; init; }
    public string? IntroducedEarliest { get; init; }
    public string? IntroducedLatest { get; init; }
    public string? DiscontinuedEdtf { get; init; }
    public string? DiscontinuedEarliest { get; init; }
    public string? DiscontinuedLatest { get; init; }

    public string? TrackMaterial { get; init; }
    public string? SupportMaterial { get; init; }
    public string? Restraints { get; init; }
    public string? Classes { get; init; }
    public string? Containment { get; init; }
    public string? SlideMaterial { get; init; }
    public string? RidersTube { get; init; }
    public string? Types { get; init; }
    public string? LiftLaunch { get; init; }

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
    public string? IntroducedEdtf { get; init; }
    public string? IntroducedEarliest { get; init; }
    public string? IntroducedLatest { get; init; }
    public string? DiscontinuedEdtf { get; init; }
    public string? DiscontinuedEarliest { get; init; }
    public string? DiscontinuedLatest { get; init; }
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

/// <summary>A main-namespace redirect: its page ID and the page it points at.</summary>
public sealed record RedirectRow
{
    public required int PageId { get; init; }
    public required int Target { get; init; }
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
    /// The 2026-10 rebuild renamed the ride drop Tallest_Drop, as on coasters.
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
            ["Drop"] = "Tallest_Drop", ["DropMetric"] = "Tallest_Drop_Metric",
            ["Elevation"] = "Diff_Elevation", ["ElevationMetric"] = "Diff_Elevation_Metric",
            ["Length"] = "Track_Length", ["LengthMetric"] = "Track_Length_Metric",
            ["Speed"] = "Speed", ["SpeedMetric"] = "Speed_Metric",
            ["Diameter"] = "Diameter", ["DiameterMetric"] = "Diameter_Metric",
            ["GForce"] = "GForce", ["RidersHour"] = "Riders_Hour",
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
    /// Coaster_Tracks' stat columns: Roller_Coasters' names, without the
    /// figures that belong to the coaster rather than a track.
    /// </summary>
    internal static readonly Dictionary<string, string> TrackStatColumns = StatColumns["Roller_Coasters"]
        .Where(c => c.Key is not ("RidersHour" or "RidersTrain" or "Tracks" or "Dimension1" or "Dimension2"))
        .ToDictionary(c => c.Key, c => c.Value);

    /// <summary>
    /// AttractionRow property → column, for the columns only some tables have.
    /// The rest of the row reads the same column on every table.
    /// </summary>
    internal static readonly Dictionary<string, Dictionary<string, string>> AttractionColumns = new()
    {
        ["Roller_Coasters"] = new()
        {
            ["Owner"] = "Owner", ["Restraints"] = "Restraints__full",
            ["Propulsion"] = "Class", ["TrackMaterial"] = "Track_Material", ["SupportMaterial"] = "Support_Material",
            ["TrainManufacturer"] = "Train_Manufacturer",
            ["Category2"] = "Category2", ["Category3"] = "Category3", ["Category4"] = "Category4",
            ["Category5"] = "Category5", ["Category6"] = "Category6",
        },
        ["Rides"] = new()
        {
            ["Owner"] = "Owner", ["Restraints"] = "Restraints__full",
            ["Indoor"] = "Indoor", ["Enclosed"] = "Enclosed_Ride", ["DarkRide"] = "Dark_Ride",
            ["Cabins"] = "Cabins", ["NumberOfRides"] = "Number_Of_Rides",
            ["Coord2Lat"] = "Coord2__lat", ["Coord2Lon"] = "Coord2__lon", ["Coord3Lat"] = "Coord3__lat", ["Coord3Lon"] = "Coord3__lon",
            ["Coord4Lat"] = "Coord4__lat", ["Coord4Lon"] = "Coord4__lon", ["Coord5Lat"] = "Coord5__lat", ["Coord5Lon"] = "Coord5__lon",
            ["Coord6Lat"] = "Coord6__lat", ["Coord6Lon"] = "Coord6__lon",
        },
        // No Owner: water slides never travel (DR-006).
        ["Water_Slides"] = new()
        {
            ["SlideClasses"] = "Class__full", ["Environment"] = "Environment", ["Containment"] = "Containment",
            ["ExitType"] = "Exit_Type", ["NumberOfSlides"] = "Number_Of_Slides", ["MaxWeightPerTube"] = "Max_Weight_Per_Tube",
            ["Category2"] = "Category2", ["Category3"] = "Category3", ["Category4"] = "Category4",
            ["Category5"] = "Category5", ["Category6"] = "Category6",
        },
    };

    /// <summary>The AttractionRow properties AttractionColumns can supply, in SELECT order.</summary>
    internal static readonly string[] AttractionOptional =
    [
        "Owner", "Restraints", "Propulsion", "TrackMaterial", "SupportMaterial",
        "Indoor", "Enclosed", "DarkRide", "Cabins", "NumberOfRides",
        "Coord2Lat", "Coord2Lon", "Coord3Lat", "Coord3Lon", "Coord4Lat", "Coord4Lon", "Coord5Lat", "Coord5Lon", "Coord6Lat", "Coord6Lon",
        "SlideClasses", "Environment", "Containment", "ExitType", "NumberOfSlides", "MaxWeightPerTube",
        "TrainManufacturer", "Category2", "Category3", "Category4", "Category5", "Category6",
    ];

    /// <summary>
    /// Product type → its product table, its variant table, and the capability
    /// list fields it declares (ProductRow property → column). One row per page in
    /// each, and no page in two (checked 2026-10-12: 257 + 713 + 122).
    /// </summary>
    internal static readonly IReadOnlyList<(string Type, string Table, string Variants, Dictionary<string, string> Lists)> ProductTables =
    [
        ("coaster", "Roller_Coaster_Products", "Roller_Coaster_Product_Variants",
            new()
            {
                ["TrackMaterial"] = "Track_Material", ["SupportMaterial"] = "Support_Material", ["Restraints"] = "Restraints",
                ["Types"] = "Type", ["LiftLaunch"] = "Lift_Launch",
            }),
        ("ride", "Ride_Products", "Ride_Product_Variants",
            new() { ["Restraints"] = "Restraints" }),
        ("water_slide", "Water_Slide_Products", "Water_Slide_Product_Variants",
            new()
            {
                ["Classes"] = "Class", ["Containment"] = "Containment", ["SlideMaterial"] = "Slide_Material",
                ["RidersTube"] = "Riders_Tube", ["Types"] = "Type",
            }),
    ];

    internal static readonly string[] ListProperties =
        ["TrackMaterial", "SupportMaterial", "Restraints", "Classes", "Containment", "SlideMaterial", "RidersTube", "Types", "LiftLaunch"];

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

    /// <summary>
    /// {{Placeholder title}}'s category in both spellings: a save stores it with
    /// spaces, Cargo's category hooks with underscores.
    /// </summary>
    internal static readonly string[] PlaceholderCategories =
        ["Articles with a placeholder title", "Articles_with_a_placeholder_title"];

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

        // The child tables first, attached by page as a product's variants are.
        var past = new List<PastLocationRow>();
        var elements = new List<ElementRow>();
        var components = new List<ComponentRow>();
        var names = new List<HistoryRow>();
        var tracks = new List<TrackRow>();
        // Until the wiki creates Coaster_Tracks, a query naming it would fail every pass.
        var hasTracks = await TableExistsAsync(conn, tx, "Coaster_Tracks", ct);
        foreach (var ids in Chunks(pageIds))
        {
            past.AddRange(await QueryAsync<PastLocationRow>(conn, tx, $"""
                SELECT t._pageID AS PageId, t.Ordering, t.LocationKind, t.OnLoan, t.Park, t.Owner, t.Location, t.State, t.Country,
                       t.PastCoord__lat AS Lat, t.PastCoord__lon AS Lon,
                       t.Opened_EDTF AS OpenedEdtf,
                       CAST(t.Opened_Earliest AS CHAR) AS OpenedEarliest, CAST(t.Opened_Latest AS CHAR) AS OpenedLatest,
                       t.Closed_EDTF AS ClosedEdtf,
                       CAST(t.Closed_Earliest AS CHAR) AS ClosedEarliest, CAST(t.Closed_Latest AS CHAR) AS ClosedLatest
                FROM {Cargo("Roller_Coaster_Past_Locations")} t
                WHERE t._pageNamespace = 0 {PageFilter("t._pageID", ids)}
                ORDER BY t._pageID, t.Ordering, t._ID
                """, new { ids }, ct));
            elements.AddRange(await QueryAsync<ElementRow>(conn, tx, $"""
                SELECT t._pageID AS PageId, CAST(t.Element__full AS CHAR) AS Elements
                FROM {Cargo("Coaster_Elements")} t
                WHERE t._pageNamespace = 0 {PageFilter("t._pageID", ids)}
                ORDER BY t._pageID, t._ID
                """, new { ids }, ct));
            components.AddRange(await QueryAsync<ComponentRow>(conn, tx, $"""
                SELECT t._pageID AS PageId, t.Ordering, t.Product, t.Product_Variant AS Variant
                FROM {Cargo("Water_Slide_Components")} t
                WHERE t._pageNamespace = 0 {PageFilter("t._pageID", ids)}
                ORDER BY t._pageID, t.Ordering, t._ID
                """, new { ids }, ct));
            // LocationOrdering is set only on attraction rows: a park's names never come back here.
            names.AddRange(await QueryAsync<HistoryRow>(conn, tx, $"""
                SELECT t._pageID AS PageId, t.Type, t.Name,
                       CAST(t.Start AS CHAR) AS Start, t.Start__precision AS StartPrecision,
                       CAST(t.`End` AS CHAR) AS `End`, t.End__precision AS EndPrecision, t.Position, t.LocationOrdering
                FROM {Cargo("History")} t
                WHERE t._pageNamespace = 0 AND t.Type = 'name' AND t.LocationOrdering >= 0 {PageFilter("t._pageID", ids)}
                ORDER BY t._pageID, t.LocationOrdering, t.Position, t._ID
                """, new { ids }, ct));
            if (hasTracks)
            {
                tracks.AddRange(await QueryAsync<TrackRow>(conn, tx, $"""
                    SELECT t._pageID AS PageId, t.Ordering, CAST(t.Name AS CHAR) AS Name,
                           {Select(StatProperties, TrackStatColumns)}
                    FROM {Cargo("Coaster_Tracks")} t
                    WHERE t._pageNamespace = 0 {PageFilter("t._pageID", ids)}
                    ORDER BY t._pageID, t.Ordering, t._ID
                    """, new { ids }, ct, (row, stats) => row with { Stats = stats }));
            }
        }

        var pastByPage = past.ToLookup(r => r.PageId);
        // One Coaster_Elements row per page on 2026-10-12 (1,722); a second would be a second infobox's.
        var elementsByPage = elements.GroupBy(r => r.PageId).ToDictionary(g => g.Key, g => g.First().Elements);
        var componentsByPage = components.ToLookup(r => r.PageId);
        var namesByPage = names.ToLookup(r => r.PageId);
        var tracksByPage = tracks.ToLookup(r => r.PageId);

        foreach (var (type, table) in AttractionTables)
        {
            foreach (var ids in Chunks(pageIds))
            {
                var found = await QueryAsync<AttractionRow>(conn, tx, $"""
                    SELECT @type AS Type, t._pageID AS PageId, t._pageName AS PageName, {Revision},
                           t.Name,
                           CAST(EXISTS (SELECT 1 FROM {Cargo("_pageData")} d
                                   JOIN {Cargo("_pageData___categories")} c ON c._rowID = d._ID
                                   WHERE d._pageID = t._pageID AND c._value IN @placeholder) AS SIGNED) AS PlaceholderTitle,
                           t.Status, t.Country, t.Manufacturer,
                           t.Designer, t.Builder, t.Replaced, t.Replacement, t.Category1,
                           CAST(t.Product__full AS CHAR) AS Product, CAST(t.Product_Variant__full AS CHAR) AS ProductVariant,
                           t.Coord__lat AS Lat, t.Coord__lon AS Lon,
                           t.LocationKind, t.OnLoan, t.Park, t.Location, t.State, t.Section, t.Subsection,
                           t.Opened_EDTF AS OpenedEdtf,
                           CAST(t.Opened_Earliest AS CHAR) AS OpenedEarliest, CAST(t.Opened_Latest AS CHAR) AS OpenedLatest,
                           t.Closed_EDTF AS ClosedEdtf,
                           CAST(t.Closed_Earliest AS CHAR) AS ClosedEarliest, CAST(t.Closed_Latest AS CHAR) AS ClosedLatest,
                           CAST(t.Opening AS CHAR) AS Opening, t.Opening__precision AS OpeningPrecision,
                           CAST(t.Closing AS CHAR) AS Closing, t.Closing__precision AS ClosingPrecision,
                           t.Min_Height AS MinHeight, t.Min_Height_Driver AS MinHeightDriver,
                           t.Min_Height_Standing AS MinHeightStanding, t.Min_Height_Upside_Down AS MinHeightUpsideDown,
                           t.Min_Unaccompanied_Height AS MinHeightUnaccompanied, t.Max_Height_Without_Child AS MaxHeightWithoutChild,
                           t.Max_Height AS MaxHeight, t.Min_Age AS MinAge, t.Min_Age_Driver AS MinAgeDriver,
                           t.Min_Unaccompanied_Age AS MinAgeUnaccompanied, t.Max_Age_Without_Child AS MaxAgeWithoutChild,
                           t.Max_Age AS MaxAge, t.Children_Only AS ChildrenOnly, t.Adult_Only_With_Child AS AdultOnlyWithChild,
                           t.Min_Weight AS MinWeight, t.Max_Weight AS MaxWeight,
                           t.Video, t.Video2, t.Video3,
                           {Select(ColumnProperties(typeof(AttractionRow), AttractionOptional), AttractionColumns[table])},
                           {Select(StatProperties, StatColumns[table])}
                    FROM {Cargo(table)} t
                    JOIN {Page} p ON p.page_id = t._pageID
                    WHERE t._pageNamespace = 0 {PageFilter("t._pageID", ids)}
                    """, new { type, ids, placeholder = PlaceholderCategories }, ct, (row, stats) => row with { Stats = stats });
                rows.AddRange(found.Select(r => r with
                {
                    PastLocations = [.. pastByPage[r.PageId]],
                    Elements = elementsByPage.GetValueOrDefault(r.PageId)?.Split('#').Select(e => e.Trim()).Where(e => e.Length > 0).ToList() ?? [],
                    Components = [.. componentsByPage[r.PageId]],
                    Names = [.. namesByPage[r.PageId]],
                    TrackRows = [.. tracksByPage[r.PageId]],
                }));
            }
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    private sealed record ElementRow
    {
        public required int PageId { get; init; }
        public string? Elements { get; init; }
    }

    /// <summary>
    /// Every park row, or only those of <paramref name="pageIds"/>. Amusement_Parks
    /// is rebuilt with A7 and A9 (Status as a String, the EDTF date columns), so
    /// this read fails loudly against a table that is not.
    /// </summary>
    public async Task<List<ParkRow>> ReadParksAsync(MySqlConnection conn, IReadOnlyCollection<int>? pageIds, CancellationToken ct)
    {
        var rows = new List<ParkRow>();
        var history = new List<HistoryRow>();
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
            history.AddRange(await QueryAsync<HistoryRow>(conn, tx, $"""
                SELECT t._pageID AS PageId, t.Type, t.Name,
                       CAST(t.Start AS CHAR) AS Start, t.Start__precision AS StartPrecision,
                       CAST(t.`End` AS CHAR) AS `End`, t.End__precision AS EndPrecision, t.Position, t.LocationOrdering
                FROM {Cargo("History")} t
                WHERE t._pageNamespace = 0 {PageFilter("t._pageID", ids)}
                  AND (t.Type IN ('owner', 'operator') OR (t.Type = 'name' AND t.LocationOrdering IS NULL))
                ORDER BY t._pageID, t.Type, t.Position, t._ID
                """, new { ids }, ct));
        }

        await tx.CommitAsync(ct);
        var byPage = history.ToLookup(h => h.PageId);
        return rows.Select(r => r with { History = [.. byPage[r.PageId]] }).ToList();
    }

    /// <summary>Every park company row, or only those of <paramref name="pageIds"/>.</summary>
    public async Task<List<ParkCompanyRow>> ReadParkCompaniesAsync(MySqlConnection conn, IReadOnlyCollection<int>? pageIds,
        CancellationToken ct)
    {
        var rows = new List<ParkCompanyRow>();
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        foreach (var ids in Chunks(pageIds))
        {
            rows.AddRange(await QueryAsync<ParkCompanyRow>(conn, tx, $"""
                SELECT t._pageID AS PageId, t._pageName AS PageName, {Revision},
                       t.Name, t.Status, t.Country, t.Headquarters, t.Website,
                       CAST(t.Established AS CHAR) AS Established, t.Established__precision AS EstablishedPrecision,
                       CAST(t.Closed AS CHAR) AS Closed, t.Closed__precision AS ClosedPrecision,
                       t.Predecessor, t.Successor
                FROM {Cargo("Park_Companies")} t
                JOIN {Page} p ON p.page_id = t._pageID
                WHERE t._pageNamespace = 0 {PageFilter("t._pageID", ids)}
                ORDER BY t._pageID, t._ID
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
                       CAST(t.Founded AS CHAR) AS Founded, t.Founded__precision AS FoundedPrecision,
                       t.Predecessor, t.Successor
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
                           t.Introduced_EDTF AS IntroducedEdtf,
                           CAST(t.Introduced_Earliest AS CHAR) AS IntroducedEarliest, CAST(t.Introduced_Latest AS CHAR) AS IntroducedLatest,
                           t.Discontinued_EDTF AS DiscontinuedEdtf,
                           CAST(t.Discontinued_Earliest AS CHAR) AS DiscontinuedEarliest, CAST(t.Discontinued_Latest AS CHAR) AS DiscontinuedLatest,
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
                           t.Name, t.Status, t.Manufacturer, t.Designer, t.Units,
                           {(lists.ContainsKey("Types") ? "NULL" : "CAST(t.`Type` AS CHAR)")} AS TypeText,
                           t.Introduced_EDTF AS IntroducedEdtf,
                           CAST(t.Introduced_Earliest AS CHAR) AS IntroducedEarliest, CAST(t.Introduced_Latest AS CHAR) AS IntroducedLatest,
                           t.Discontinued_EDTF AS DiscontinuedEdtf,
                           CAST(t.Discontinued_Earliest AS CHAR) AS DiscontinuedEarliest, CAST(t.Discontinued_Latest AS CHAR) AS DiscontinuedLatest,
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
    /// Every product page's ID, across the three product tables: about 1,100,
    /// read whole on every pass, so an attraction's product link can be checked
    /// against them.
    /// </summary>
    public async Task<HashSet<int>> ReadProductIdsAsync(MySqlConnection conn, CancellationToken ct)
    {
        var sql = string.Join("\nUNION ALL\n", ProductTables.Select(p =>
            $"SELECT t._pageID FROM {Cargo(p.Table)} t WHERE t._pageNamespace = 0"));
        return [.. await conn.QueryAsync<int>(new CommandDefinition(sql, cancellationToken: ct))];
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

    /// <summary>Whether the Cargo database holds <paramref name="table"/> yet.</summary>
    private async Task<bool> TableExistsAsync(MySqlConnection conn, MySqlTransaction? tx, string table, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @schema AND table_name = @table",
            new { schema = _config.CargoDatabase, table }, tx, cancellationToken: ct)) > 0;

    /// <summary>
    /// Every main-namespace redirect and the page it points at, one hop, as
    /// Cargo's _pageData follows it (4,474 on 2026-10-19).
    /// </summary>
    public async Task<List<RedirectRow>> ReadRedirectsAsync(MySqlConnection conn, CancellationToken ct) =>
        await QueryAsync<RedirectRow>(conn, null, $"""
            SELECT d._pageID AS PageId, d._pageIDOrRedirect AS Target
            FROM {Cargo("_pageData")} d
            JOIN {Page} p ON p.page_id = d._pageID
            WHERE d._pageNamespace = 0 AND p.page_is_redirect <> 0 AND d._pageIDOrRedirect <> d._pageID
            """, null, ct);

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
