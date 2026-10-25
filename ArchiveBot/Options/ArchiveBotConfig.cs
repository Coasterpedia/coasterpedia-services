namespace CoasterpediaServices.ArchiveBot.Options;

public record ArchiveBotConfig
{
    public required string BotUsername { get; init; }
    public required string BotPassword { get; init; }
    public static readonly TimeSpan Delay = TimeSpan.FromHours(6);
    public const string ArchiveQueue = "archive";
    // Taken by the archive server only when ArchiveQueue is empty, so edits always go first.
    public const string SweepQueue = "archive-sweep";
    public ArchiveSweepOptions Sweep { get; init; } = new();
    public static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(10);
    // 15 CDX searches a minute, the limit other CDX clients report; 40 a minute was answered with 429s.
    // Each citation without an archive takes up to four.
    public static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(5);
}