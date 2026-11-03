namespace CoasterpediaServices.ArchiveBot.Options;

public record ArchiveBotConfig
{
    public required string BotUsername { get; init; }
    public required string BotPassword { get; init; }
    // The archive.org account's S3 keys (archive.org/account/s3.php), for Save Page Now's capture API. Without them
    // every capture fails as error:unauthorized, which pauses all saves.
    public string? ArchiveAccessKey { get; init; }
    public string? ArchiveSecretKey { get; init; }
    public static readonly TimeSpan Delay = TimeSpan.FromHours(6);
    public const string ArchiveQueue = "archive";
    // Sources of older files, from edits to their pages. Taken only when ArchiveQueue is empty.
    public const string FileBackfillQueue = "archive-files";
    // Taken only when both queues above are empty, so edits always go first and the sweep pauses for a file backlog.
    public const string SweepQueue = "archive-sweep";
    public ArchiveSweepOptions Sweep { get; init; } = new();
    // Between one capture finishing and the next starting, which keeps us under SPN2's 7 captures a minute.
    public static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(10);
    // At most one save a host this often. Flickr saves 10 s apart failed in runs, each run pausing the host for
    // HostPause (Oct 2026); a steady trickle should drain a backlog faster than bursts and pauses.
    public static readonly TimeSpan HostSaveInterval = TimeSpan.FromMinutes(1);
    // Save Page Now failing this many captures running on one host (Flickr did, Oct 2026) stops saves there for
    // HostPause. Each failure otherwise still costs the worker a save and its pacing.
    public const int HostFailuresBeforePause = 3;
    public static readonly TimeSpan HostPause = TimeSpan.FromHours(6);
    // Captures failing on this many hosts with none working in between is Save Page Now turning us away, not the
    // sites: it refused Commons, Flickr, Geograph and Facebook at once (Nov 2026), and pausing each host for
    // HostPause cost most of a day. Saves everywhere stop for GlobalPause instead, and no host is paused for it.
    // A failure Save Page Now puts down to itself or our account (CaptureFault.SaveService) stops them at once.
    public const int FailingHostsBeforeGlobalPause = 3;
    public static readonly TimeSpan GlobalPause = TimeSpan.FromMinutes(30);
    // A failed upload's next try, from the backfill queue rather than another turn at the front of the line.
    public static readonly TimeSpan FailedUploadRetry = TimeSpan.FromDays(1);
    // 15 CDX searches a minute, the limit other CDX clients report; 40 a minute was answered with 429s.
    // Each citation without an archive takes up to four.
    public static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(5);
}