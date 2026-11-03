using CoasterpediaServices.ArchiveBot.Clients.Archive;
using CoasterpediaServices.ArchiveBot.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CoasterpediaServices.ArchiveBot.Tests;

public class ArchiveClientTests
{
    private static (PacedArchiveClient Client, FakeArchive Archive, FixedTime Time) Paced()
    {
        var archive = new FakeArchive();
        var time = new FixedTime(DateTimeOffset.Parse("2026-10-27T20:00:00Z"));
        return (new PacedArchiveClient(archive, NullLogger<PacedArchiveClient>.Instance, TimeSpan.Zero, TimeSpan.Zero, time), archive, time);
    }

    [Fact]
    public async Task Repeated_failed_captures_pause_saves_to_that_host()
    {
        var (client, archive, time) = Paced();
        archive.Failing("www.flickr.com");

        for (var i = 1; i < ArchiveBotConfig.HostFailuresBeforePause; i++)
        {
            await client.SavePage($"https://www.flickr.com/photos/a/{i}/");
            Assert.Null(client.PausedUntil("https://www.flickr.com/photos/b/1/"));
        }

        await client.SavePage("https://www.flickr.com/photos/a/last/");
        Assert.Equal(time.Now + ArchiveBotConfig.HostPause, client.PausedUntil("https://www.flickr.com/photos/b/1/"));
        Assert.Null(client.PausedUntil("https://wikimapia.org/1/photo/2"));

        time.Advance(ArchiveBotConfig.HostPause);
        Assert.Null(client.PausedUntil("https://www.flickr.com/photos/b/1/"));

        // The try after the pause fails, and pauses the host again straight away.
        await client.SavePage("https://www.flickr.com/photos/a/again/");
        Assert.NotNull(client.PausedUntil("https://www.flickr.com/photos/b/1/"));
    }

    [Fact]
    public async Task Unexplained_failures_across_hosts_pause_every_host_briefly()
    {
        var (client, archive, time) = Paced();
        string[] hosts = ["www.flickr.com", "commons.wikimedia.org", "www.geograph.org.uk"];
        foreach (var host in hosts)
        {
            archive.Failing(host, "http-500");
            await client.SavePage($"https://{host}/a/1");
        }

        Assert.Equal(time.Now + ArchiveBotConfig.GlobalPause, client.PausedUntil("https://wikimapia.org/1/photo/2"));
        Assert.Equal(time.Now + ArchiveBotConfig.GlobalPause, client.Reserve("https://wikimapia.org/1/photo/2"));

        // No host is left paused for the hours a failing site gets.
        time.Advance(ArchiveBotConfig.GlobalPause);
        foreach (var host in hosts)
        {
            Assert.Null(client.PausedUntil($"https://{host}/b/1"));
        }

        // The try after the pause fails, and pauses every host again straight away.
        await client.SavePage("https://www.flickr.com/a/2");
        Assert.Equal(time.Now + ArchiveBotConfig.GlobalPause, client.PausedUntil("https://wikimapia.org/1/photo/2"));
    }

    [Fact]
    public async Task A_capture_that_works_on_any_host_means_the_failures_are_the_sites()
    {
        var (client, archive, _) = Paced();
        archive.Failing("www.flickr.com", "http-500");
        archive.Failing("commons.wikimedia.org", "http-500");
        archive.Failing("www.geograph.org.uk", "http-500");

        await client.SavePage("https://www.flickr.com/a/1");
        await client.SavePage("https://commons.wikimedia.org/a/1");
        await client.SavePage("https://wikimapia.org/1/photo/2");
        await client.SavePage("https://www.geograph.org.uk/a/1");

        Assert.Null(client.PausedUntil("https://wikimapia.org/1/photo/3"));
    }

    [Fact]
    public async Task Sites_failing_one_after_another_for_their_own_reasons_pause_only_themselves()
    {
        var (client, archive, _) = Paced();
        string[] hosts = ["www.dollywood.com", "yomiuriland.com", "seaworldparks.com", "www.fabbrigroup.com"];
        foreach (var host in hosts)
        {
            archive.Failing(host, "error:gateway-timeout");
            await client.SavePage($"https://{host}/a/1");
        }

        Assert.Null(client.PausedUntil("https://wikimapia.org/1/photo/2"));
    }

    [Fact]
    public async Task A_host_save_page_now_is_resting_pauses_nothing()
    {
        var (client, archive, _) = Paced();
        archive.Failing("www.flickr.com", "error:host-crawling-paused");

        for (var i = 0; i < ArchiveBotConfig.HostFailuresBeforePause + 1; i++)
        {
            await client.SavePage($"https://www.flickr.com/a/{i}");
        }

        Assert.Null(client.PausedUntil("https://www.flickr.com/b/1"));
    }

    [Fact]
    public async Task Save_Page_Now_at_fault_pauses_every_host_at_once()
    {
        var (client, archive, time) = Paced();
        archive.Failing("www.flickr.com", "error:user-session-limit");

        await client.SavePage("https://www.flickr.com/a/1");

        Assert.Equal(time.Now + ArchiveBotConfig.GlobalPause, client.PausedUntil("https://wikimapia.org/1/photo/2"));
    }

    [Fact]
    public async Task A_url_Save_Page_Now_wont_capture_pauses_nothing()
    {
        var (client, archive, _) = Paced();
        archive.Failing("www.flickr.com", "error:blocked-url");

        for (var i = 0; i < ArchiveBotConfig.HostFailuresBeforePause + 1; i++)
        {
            await client.SavePage($"https://www.flickr.com/a/{i}");
        }

        Assert.Null(client.PausedUntil("https://www.flickr.com/b/1"));
    }

    [Fact]
    public async Task A_capture_that_works_clears_the_failures()
    {
        var failing = new FakeArchive();
        failing.Failing("www.flickr.com");
        var flaky = new PacedArchiveClient(new Alternating(failing, new FakeArchive()), NullLogger<PacedArchiveClient>.Instance,
            TimeSpan.Zero, TimeSpan.Zero, TimeProvider.System);

        for (var i = 0; i < ArchiveBotConfig.HostFailuresBeforePause * 2; i++)
        {
            await flaky.SavePage($"https://www.flickr.com/photos/a/{i}/");
        }

        Assert.Null(flaky.PausedUntil("https://www.flickr.com/photos/b/1/"));
    }

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private static (PacedArchiveClient Client, FakeArchive Archive, FixedTime Time) Spaced()
    {
        var archive = new FakeArchive();
        var time = new FixedTime(DateTimeOffset.Parse("2026-10-30T20:00:00Z"));
        return (new PacedArchiveClient(archive, NullLogger<PacedArchiveClient>.Instance, TimeSpan.Zero, Interval, time), archive, time);
    }

    [Fact]
    public void A_backlog_on_one_host_gets_slots_an_interval_apart()
    {
        var (client, _, time) = Spaced();
        var start = time.Now;

        Assert.Null(client.Reserve("https://www.flickr.com/photos/a/1/"));
        Assert.Equal(start + Interval, client.Reserve("https://www.flickr.com/photos/a/2/"));
        Assert.Equal(start + Interval * 2, client.Reserve("https://www.flickr.com/photos/a/3/"));
        // Asking again doesn't move a job to the back of the line.
        Assert.Equal(start + Interval, client.Reserve("https://www.flickr.com/photos/a/2/"));
        Assert.Null(client.Reserve("https://wikimapia.org/1/photo/2"));

        time.Advance(Interval);
        Assert.Null(client.Reserve("https://www.flickr.com/photos/a/2/"));
        Assert.Equal(start + Interval * 3, client.Reserve("https://www.flickr.com/photos/a/4/"));
    }

    [Fact]
    public async Task A_save_without_a_slot_still_holds_back_the_next_slot()
    {
        var (client, _, time) = Spaced();

        await client.SavePage("https://www.flickr.com/photos/a/1/");

        Assert.Equal(time.Now + Interval, client.Reserve("https://www.flickr.com/photos/a/2/"));
    }

    [Fact]
    public async Task Slots_resume_after_a_pause()
    {
        var (client, archive, time) = Spaced();
        archive.Failing("www.flickr.com");
        for (var i = 0; i < ArchiveBotConfig.HostFailuresBeforePause; i++)
        {
            Assert.Null(client.Reserve($"https://www.flickr.com/photos/a/{i}/"));
            await client.SavePage($"https://www.flickr.com/photos/a/{i}/");
            time.Advance(Interval);
        }

        var pausedUntil = client.PausedUntil("https://www.flickr.com/photos/b/1/")!.Value;
        Assert.Equal(pausedUntil, client.Reserve("https://www.flickr.com/photos/b/1/"));
        Assert.Equal(pausedUntil + Interval, client.Reserve("https://www.flickr.com/photos/b/2/"));

        time.Advance(ArchiveBotConfig.HostPause);
        Assert.Null(client.Reserve("https://www.flickr.com/photos/b/1/"));
    }

    [Fact]
    public async Task A_job_back_during_a_pause_takes_a_slot_after_it()
    {
        var (client, archive, time) = Spaced();
        Assert.Null(client.Reserve("https://www.flickr.com/photos/a/0/"));
        var held = client.Reserve("https://www.flickr.com/photos/b/1/")!.Value;

        archive.Failing("www.flickr.com");
        for (var i = 0; i < ArchiveBotConfig.HostFailuresBeforePause; i++)
        {
            await client.SavePage($"https://www.flickr.com/photos/a/{i}/");
            time.Advance(Interval);
        }

        Assert.True(time.Now >= held);
        Assert.Equal(client.PausedUntil("https://www.flickr.com/photos/b/1/"), client.Reserve("https://www.flickr.com/photos/b/1/"));
    }

    [Fact]
    public async Task A_job_back_late_at_its_slot_waits_out_the_interval_after_the_last_save()
    {
        var interval = TimeSpan.FromMilliseconds(300);
        var client = new PacedArchiveClient(new FakeArchive(), NullLogger<PacedArchiveClient>.Instance, TimeSpan.Zero, interval,
            TimeProvider.System);
        Assert.Null(await client.TakeTurn("https://www.flickr.com/photos/a/1/"));
        var slot = (await client.TakeTurn("https://www.flickr.com/photos/a/2/"))!.Value;

        // A citation saves on the host while the backlog job is away, just before its slot comes round.
        await Task.Delay(slot - TimeProvider.System.GetUtcNow());
        await client.SavePage("https://www.flickr.com/photos/cited/");
        var started = TimeProvider.System.GetTimestamp();

        Assert.Null(await client.TakeTurn("https://www.flickr.com/photos/a/2/"));
        Assert.True(TimeProvider.System.GetElapsedTime(started) >= interval - TimeSpan.FromMilliseconds(20));
    }

    [Fact]
    public void Waiting_for_a_slot_is_decided_before_a_retry_is_counted()
    {
        Assert.True(new DeferAttribute().Order < new Hangfire.AutomaticRetryAttribute().Order);
    }

    // Fails, then works, then fails.
    private sealed class Alternating(ISavePageNow first, ISavePageNow second) : ISavePageNow
    {
        private int _calls;

        public Task<Capture> Save(string url) => _calls++ % 2 == 0 ? first.Save(url) : second.Save(url);
    }
}
