using System.Collections.Concurrent;
using StackExchange.Redis;

namespace CoasterpediaServices.ArchiveBot.Sweep;

// When ArchiveLinkJob last went through each article, by page ID so a move keeps its place.
public interface IPageCheckStore
{
    Task MarkChecked(long pageId, DateTimeOffset at);

    Task<Dictionary<long, DateTimeOffset>> All();

    Task Forget(IReadOnlyCollection<long> pageIds);
}

/// <summary>
/// A sorted set (archivebot:pages) scored by Unix time, beside the link states. Losing it is safe:
/// the sweep sees every article as never checked and goes through them all again, and the link
/// states keep that from fetching anything early.
/// </summary>
public sealed class RedisPageCheckStore : IPageCheckStore
{
    private const string PagesKey = "archivebot:pages";

    private readonly IConnectionMultiplexer _redis;

    public RedisPageCheckStore(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    private IDatabase Db => _redis.GetDatabase();

    public Task MarkChecked(long pageId, DateTimeOffset at) => Db.SortedSetAddAsync(PagesKey, pageId, at.ToUnixTimeSeconds());

    public async Task<Dictionary<long, DateTimeOffset>> All() =>
        (await Db.SortedSetRangeByRankWithScoresAsync(PagesKey))
        .ToDictionary(e => (long)e.Element, e => DateTimeOffset.FromUnixTimeSeconds((long)e.Score));

    public Task Forget(IReadOnlyCollection<long> pageIds) =>
        Db.SortedSetRemoveAsync(PagesKey, pageIds.Select(id => (RedisValue)id).ToArray());
}

// For tests and the local runner, which have no Redis.
public sealed class InMemoryPageCheckStore : IPageCheckStore
{
    private readonly ConcurrentDictionary<long, DateTimeOffset> _checked = new();

    public Task MarkChecked(long pageId, DateTimeOffset at)
    {
        _checked[pageId] = at;
        return Task.CompletedTask;
    }

    public Task<Dictionary<long, DateTimeOffset>> All() => Task.FromResult(new Dictionary<long, DateTimeOffset>(_checked));

    public Task Forget(IReadOnlyCollection<long> pageIds)
    {
        foreach (var id in pageIds)
        {
            _checked.TryRemove(id, out _);
        }

        return Task.CompletedTask;
    }
}
