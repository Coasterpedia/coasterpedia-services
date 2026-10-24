namespace CoasterpediaServices.ArchiveBot.Options;

/// <summary>
/// The pass over articles nobody has edited (ArchiveSweepJob). Bound as ArchiveBotConfig:Sweep.
/// </summary>
public record ArchiveSweepOptions
{
    /// <summary>Off until switched on: the first pass reaches every citing article, and edits most of them.</summary>
    public bool Enabled { get; init; }

    /// <summary>Minutes between runs. Divide 60 by it evenly, or the runs drift within the hour.</summary>
    public int IntervalMinutes { get; init; } = 30;

    /// <summary>
    /// How long one pass over every citing article takes, and how recently an article can have been
    /// checked and still be skipped. Each run queues its share of the cycle, never more, so the checks
    /// spread evenly across it and stay spread. The alive-link recheck interval: sooner, and every
    /// link on the page would still be within its own.
    /// </summary>
    public TimeSpan Cycle { get; init; } = TimeSpan.FromDays(7);
}
