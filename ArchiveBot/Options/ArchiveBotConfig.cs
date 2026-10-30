namespace CoasterpediaServices.ArchiveBot.Options;

public record ArchiveBotConfig
{
    public required string BotUsername { get; init; }
    public required string BotPassword { get; init; }
    public static readonly TimeSpan Delay = TimeSpan.FromHours(6);
    public const string ArchiveQueue = "archive";
    // Sources of older files, from edits to their pages. Taken only when ArchiveQueue is empty.
    public const string FileBackfillQueue = "archive-files";
    // Taken only when both queues above are empty, so edits always go first and the sweep pauses for a file backlog.
    public const string SweepQueue = "archive-sweep";
    public ArchiveSweepOptions Sweep { get; init; } = new();
    public static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(10);
    // At most one save a host this often. Flickr saves 10 s apart failed in runs, each run pausing the host for
    // HostPause (Oct 2026); a steady trickle should drain a backlog faster than bursts and pauses.
    public static readonly TimeSpan HostSaveInterval = TimeSpan.FromMinutes(1);
    // Save Page Now failing this many captures running on one host (Flickr did, Oct 2026) stops saves there for
    // HostPause. Each failure otherwise still costs the worker a save and its pacing.
    public const int HostFailuresBeforePause = 3;
    public static readonly TimeSpan HostPause = TimeSpan.FromHours(6);
    // A failed upload's next try, from the backfill queue rather than another turn at the front of the line.
    public static readonly TimeSpan FailedUploadRetry = TimeSpan.FromDays(1);
    // 15 CDX searches a minute, the limit other CDX clients report; 40 a minute was answered with 429s.
    // Each citation without an archive takes up to four.
    public static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(5);
}