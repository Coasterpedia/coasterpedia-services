namespace CoasterpediaServices.ArchiveBot.Sweep;

public record CitingArticle(long PageId, string Title);

public record SweepBatch(IReadOnlyList<CitingArticle> Pages, int Due, IReadOnlyList<long> Gone);

public static class SweepPlan
{
    // One run's share of the cycle. Going faster than this would only bunch the checks: each
    // article comes due again one cycle after its last check, so the first pass's shape repeats.
    public static int Quota(int citing, TimeSpan interval, TimeSpan cycle) =>
        citing == 0 ? 0 : (int)Math.Ceiling(citing * interval.TotalMinutes / cycle.TotalMinutes);

    // The next articles to go through: never checked first, in page ID order, then the longest
    // since their last check. One checked within minimumAge isn't due. Gone lists the pages with
    // a check time that no longer cite anything (deleted, or their citations removed).
    public static SweepBatch Pick(IReadOnlyCollection<CitingArticle> citing, IReadOnlyDictionary<long, DateTimeOffset> lastChecked,
        DateTimeOffset now, TimeSpan minimumAge, int batchSize)
    {
        var due = citing
            .DistinctBy(p => p.PageId)
            .Select(p => (Page: p, Checked: lastChecked.TryGetValue(p.PageId, out var at) ? at : (DateTimeOffset?)null))
            .Where(p => p.Checked == null || now - p.Checked >= minimumAge)
            .OrderBy(p => p.Checked ?? DateTimeOffset.MinValue)
            .ThenBy(p => p.Page.PageId)
            .Select(p => p.Page)
            .ToList();

        var citingIds = citing.Select(p => p.PageId).ToHashSet();
        var gone = lastChecked.Keys.Where(id => !citingIds.Contains(id)).Order().ToList();

        return new SweepBatch(due.Take(batchSize).ToList(), due.Count, gone);
    }
}
