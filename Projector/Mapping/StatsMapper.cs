using System.Globalization;
using System.Text.RegularExpressions;
using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Model;

namespace CoasterpediaServices.Projector.Mapping;

/// <summary>
/// The stats block (openapi AttractionStats). Measurements come from the
/// wiki's own _Metric columns, which Module:UnitConverter fills unrounded at
/// save; the page's Units says what was typed. Text columns publish only what
/// parses, and the rest is reported.
/// </summary>
public static partial class StatsMapper
{
    private sealed record Dimension(string Metric, string Imperial, double ImperialPerMetric);

    // Module:UnitConverter's factors, so a computed value matches a stored one.
    private static readonly Dimension LengthUnit = new("m", "ft", 3.28084);
    private static readonly Dimension SpeedUnit = new("km/h", "mph", 0.621371);

    [GeneratedRegex(@"^(\d+):([0-5]\d)$")]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"^(\d{1,3}(,\d{3})+|\d+)$")]
    private static partial Regex WholeNumberPattern();

    public static AttractionStats? Map(int pageId, StatsRow r, List<DataIssue> issues)
    {
        bool? imperial = r.Units?.ToLowerInvariant() switch
        {
            null or "metric" => false,
            "imperial" => true,
            _ => null,
        };
        if (imperial == null)
        {
            // The units are unknown, so no figure can be read safely.
            issues.Add(new DataIssue(pageId, "units", r.Units!, "not metric or imperial"));
        }

        Measurement? M(double? entered, double? metric, Dimension unit, string field) =>
            imperial is { } imp ? Measure(pageId, entered, metric, unit, imp, field, issues) : null;

        var stats = new AttractionStats
        {
            Height = M(r.Height, r.HeightMetric, LengthUnit, "height"),
            Drop = M(r.Drop, r.DropMetric, LengthUnit, "drop"),
            ElevationDifference = M(r.Elevation, r.ElevationMetric, LengthUnit, "elevation_difference"),
            Length = M(r.Length, r.LengthMetric, LengthUnit, "length"),
            Speed = M(r.Speed, r.SpeedMetric, SpeedUnit, "speed"),
            Diameter = M(r.Diameter, null, LengthUnit, "diameter"),
            Inversions = r.TrackInversions == null && r.RiderInversions == null
                ? null
                : new Inversions(r.TrackInversions, r.RiderInversions),
            MaxAngle = Clean(r.Angle),
            BankAngle = Clean(r.BankAngle),
            GForce = Clean(r.GForce),
            Duration = Duration(pageId, r.Duration, issues),
            RidersPerHour = WholeNumber(pageId, "riders_per_hour", r.RidersHour, issues),
            RidersPerTrain = r.RidersTrain,
            // The coaster infobox stores 1 when |tracks= is left out: the site's own reading.
            Tracks = r.Tracks,
            Footprint = r.Dimension1 == null && r.Dimension2 == null
                ? null
                : new Footprint(M(r.Dimension1, null, LengthUnit, "footprint.length"),
                    M(r.Dimension2, null, LengthUnit, "footprint.width")),
        };

        return stats.IsEmpty ? null : stats;
    }

    private static Measurement? Measure(int pageId, double? entered, double? storedMetric, Dimension unit, bool imperial,
        string field, List<DataIssue> issues)
    {
        if (entered is not { } value)
        {
            return null;
        }

        if (value <= 0)
        {
            issues.Add(new DataIssue(pageId, field, Text(value), "not a positive number"));
            return null;
        }

        var typed = Round(value, Decimals(value));
        // Imperial converts to a finer metric step than the figure had (1 ft ≈ 0.3 m),
        // so one more decimal keeps what was entered without inventing precision.
        var metric = storedMetric ?? (imperial ? value / unit.ImperialPerMetric : value);
        metric = imperial ? Round(metric, Decimals(value) + 1) : typed;

        return new Measurement(metric, unit.Metric, new Entered(typed, imperial ? unit.Imperial : unit.Metric));
    }

    /// <summary>m:ss only (all but 13 coasters, 2026-10-11); anything else is a note, not a number.</summary>
    private static Measurement? Duration(int pageId, string? text, List<DataIssue> issues)
    {
        if (text == null)
        {
            return null;
        }

        var m = DurationPattern().Match(text);
        if (!m.Success)
        {
            issues.Add(new DataIssue(pageId, "duration", text, "not m:ss"));
            return null;
        }

        var seconds = int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value);
        return seconds > 0 ? new Measurement(seconds, "s") : null;
    }

    /// <summary>A plain whole number, thousands commas allowed ("1,200"). Ranges and notes stay text on the wiki.</summary>
    private static int? WholeNumber(int pageId, string field, string? text, List<DataIssue> issues)
    {
        if (text == null)
        {
            return null;
        }

        if (WholeNumberPattern().IsMatch(text)
            && int.TryParse(text.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && n > 0)
        {
            return n;
        }

        issues.Add(new DataIssue(pageId, field, text, "not a plain number"));
        return null;
    }

    /// <summary>Strips binary noise from a stored float (4.499999999 → 4.5).</summary>
    private static double? Clean(double? v) => v is { } x ? Round(x, 6) : null;

    private static double Round(double v, int decimals) => Math.Round(v, decimals, MidpointRounding.AwayFromZero);

    /// <summary>The decimals a stored figure was typed with, read back from the float.</summary>
    internal static int Decimals(double v)
    {
        for (var d = 0; d < 6; d++)
        {
            if (Math.Abs(v - Math.Round(v, d)) < 1e-9 * Math.Max(1, Math.Abs(v)))
            {
                return d;
            }
        }

        return 6;
    }

    private static string Text(double v) => v.ToString(CultureInfo.InvariantCulture);
}
