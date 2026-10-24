using System.Reflection;
using CoasterpediaServices.ArchiveBot.Options;
using CoasterpediaServices.ArchiveBot.Sweep;
using Hangfire;
using Hangfire.Common;
using Xunit;

namespace CoasterpediaServices.ArchiveBot.Tests;

public class SweepTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T12:00:00Z");
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private static CitingArticle Page(long id) => new(id, $"Page {id}");

    private static SweepBatch Pick(IEnumerable<CitingArticle> citing, Dictionary<long, DateTimeOffset>? checkedAt = null, int batch = 50) =>
        SweepPlan.Pick(citing.ToList(), checkedAt ?? [], Now, Week, batch);

    [Fact]
    public void Articles_never_checked_go_first_in_page_order()
    {
        var batch = Pick([Page(3), Page(1), Page(2)], new() { [1] = Now - TimeSpan.FromDays(30) });

        Assert.Equal([2L, 3L, 1L], batch.Pages.Select(p => p.PageId));
    }

    [Fact]
    public void Then_the_longest_since_their_last_check()
    {
        var batch = Pick([Page(1), Page(2), Page(3)], new()
        {
            [1] = Now - TimeSpan.FromDays(8),
            [2] = Now - TimeSpan.FromDays(40),
            [3] = Now - TimeSpan.FromDays(10)
        });

        Assert.Equal([2L, 3L, 1L], batch.Pages.Select(p => p.PageId));
    }

    [Fact]
    public void An_article_checked_within_the_week_is_not_due()
    {
        var batch = Pick([Page(1), Page(2)], new()
        {
            [1] = Now - TimeSpan.FromDays(6),
            [2] = Now - Week
        });

        Assert.Equal([2L], batch.Pages.Select(p => p.PageId));
        Assert.Equal(1, batch.Due);
    }

    [Fact]
    public void A_batch_stops_at_its_size_and_counts_the_rest_as_due()
    {
        var batch = Pick(Enumerable.Range(1, 120).Select(i => Page(i)), batch: 50);

        Assert.Equal(50, batch.Pages.Count);
        Assert.Equal(120, batch.Due);
        Assert.Equal(1, batch.Pages[0].PageId);
    }

    [Fact]
    public void An_article_citing_through_two_templates_is_queued_once()
    {
        var batch = Pick([Page(1), Page(1), Page(2)]);

        Assert.Equal([1L, 2L], batch.Pages.Select(p => p.PageId));
    }

    [Fact]
    public void An_article_that_no_longer_cites_anything_is_forgotten()
    {
        var batch = Pick([Page(1)], new()
        {
            [1] = Now - TimeSpan.FromDays(1),
            [9] = Now - TimeSpan.FromDays(1)
        });

        Assert.Equal([9L], batch.Gone);
        Assert.Empty(batch.Pages);
    }

    [Theory]
    [InlineData(11830, 36)]
    [InlineData(3000, 9)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    public void A_run_queues_its_share_of_the_week(int citing, int quota)
    {
        Assert.Equal(quota, SweepPlan.Quota(citing, TimeSpan.FromMinutes(30), Week));
    }

    // Every run for four weeks, each queued article checked at once, then checks counted per day.
    private static int[] ChecksPerDay(int articles, Func<int, int> batchSize, Random? edits = null)
    {
        var citing = Enumerable.Range(1, articles).Select(i => Page(i)).ToList();
        var checkedAt = new Dictionary<long, DateTimeOffset>();
        var perDay = new int[28];
        for (var run = 0; run < 28 * 48; run++)
        {
            var now = Now + TimeSpan.FromMinutes(30 * run);
            var batch = SweepPlan.Pick(citing, checkedAt, now, Week, batchSize(articles));
            foreach (var page in batch.Pages)
            {
                checkedAt[page.PageId] = now;
                perDay[run / 48]++;
            }

            // Edits check a few articles on their own, outside the sweep.
            if (edits != null && run % 4 == 0)
            {
                checkedAt[edits.Next(1, articles + 1)] = now;
            }
        }

        return perDay;
    }

    [Fact]
    public void Paced_by_the_cycle_the_checks_stay_even_through_the_week()
    {
        var perDay = ChecksPerDay(3000, n => SweepPlan.Quota(n, TimeSpan.FromMinutes(30), Week), new Random(7));

        // The first pass is spread over the first week, and every week after keeps that spread.
        var mean = 3000 / 7.0;
        Assert.All(perDay, day => Assert.InRange(day, mean * 0.85, mean * 1.05));
    }

    [Fact]
    public void Running_as_fast_as_allowed_would_bunch_the_checks()
    {
        // 50 a run gets through 3,000 articles in 30 hours, so every later week does the same.
        var perDay = ChecksPerDay(3000, _ => 50);

        Assert.Contains(0, perDay[7..]);
        Assert.True(perDay[7..].Max() > 3000 / 7.0 * 2);
    }

    [Fact]
    public void Nothing_due_is_an_empty_batch()
    {
        var batch = Pick([Page(1)], new() { [1] = Now });

        Assert.Empty(batch.Pages);
        Assert.Equal(0, batch.Due);
    }

    // The priority only holds if each entry point names its own queue.
    [Theory]
    [InlineData(nameof(ArchiveLinkJob.RunFromSweep), new[] { typeof(string) }, ArchiveBotConfig.SweepQueue)]
    [InlineData(nameof(ArchiveLinkJob.Run), new[] { typeof(string), typeof(long?) }, ArchiveBotConfig.ArchiveQueue)]
    [InlineData(nameof(ArchiveLinkJob.Run), new[] { typeof(string) }, ArchiveBotConfig.ArchiveQueue)]
    public void Each_entry_point_runs_on_its_own_queue(string method, Type[] parameters, string queue)
    {
        var attribute = typeof(ArchiveLinkJob).GetMethod(method, parameters)!.GetCustomAttribute<QueueAttribute>();
        Assert.Equal(queue, attribute?.Queue);
    }

    [Fact]
    public async Task Rewriting_alone_records_no_check()
    {
        // Run records the check once the page is saved; a dry rewrite must not count as one.
        var h = new JobHarness();
        await h.Rewrite("No citations here.");

        Assert.Empty(await h.PageChecks.All());
    }

    private sealed class RecurringJobs : IRecurringJobManager
    {
        public Dictionary<string, (Job Job, string Cron)> Added { get; } = [];
        public List<string> Removed { get; } = [];

        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options) =>
            Added[recurringJobId] = (job, cronExpression);

        public void Trigger(string recurringJobId) => throw new NotSupportedException();

        public void RemoveIfExists(string recurringJobId) => Removed.Add(recurringJobId);
    }

    private static ArchiveBotConfig Config(bool enabled) =>
        new() { BotUsername = "ArchiveBot", BotPassword = "unused", Sweep = new ArchiveSweepOptions { Enabled = enabled } };

    [Fact]
    public void The_sweep_is_registered_only_when_enabled()
    {
        var on = new RecurringJobs();
        on.UseArchiveBotJobs(Config(true));
        Assert.Equal("*/30 * * * *", on.Added["archive-sweep"].Cron);
        Assert.Equal(nameof(ArchiveSweepJob.Run), on.Added["archive-sweep"].Job.Method.Name);

        var off = new RecurringJobs();
        off.UseArchiveBotJobs(Config(false));
        Assert.Empty(off.Added);
        Assert.Equal(["archive-sweep"], off.Removed);
    }

    [Fact]
    public void The_sweep_is_off_unless_configured()
    {
        Assert.False(new ArchiveSweepOptions().Enabled);
    }
}
