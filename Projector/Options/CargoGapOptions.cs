namespace CoasterpediaServices.Projector.Options;

/// <summary>
/// The nightly check for articles whose infobox stored no Cargo row
/// (CargoGapRepair). Bound as ProjectorConfig:Gaps, and only registered while
/// the Projector is enabled, because it reads Cargo through the same user.
/// </summary>
public record CargoGapOptions
{
    /// <summary>Half an hour before the sweep, so the sweep projects what this restores rather than deleting it.</summary>
    public string Cron { get; init; } = "47 2 * * *";

    /// <summary>
    /// The most pages one run null-edits. More gaps than this point at a cause
    /// that a null edit won't fix (a save during a __NEXT rebuild, say), so the
    /// run lists them and edits none. 0 reports only.
    /// </summary>
    public int FixLimit { get; init; } = 25;

    /// <summary>A page edited this recently may still have its store in the job queue, so it isn't a gap yet.</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Pause between null edits.</summary>
    public TimeSpan EditInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Cargo table → the infoboxes that store it. Every main-namespace page
    /// transcluding one of them should hold a row there and in _pageData.
    /// </summary>
    public Dictionary<string, string[]> Tables { get; init; } = new()
    {
        ["Roller_Coasters"] = ["Template:Infobox roller coaster", "Template:Infobox coaster multitrack"],
        ["Rides"] = ["Template:Infobox ride"],
        ["Water_Slides"] = ["Template:Infobox water slide"],
        ["Amusement_Parks"] = ["Template:Infobox amusement park"],
    };
}
