using System.Collections.Concurrent;
using StackExchange.Redis;

namespace CoasterpediaServices.ArchiveBot.Links;

public interface ILinkStateStore
{
    Task<LinkState?> Get(string url);

    Task Save(string url, LinkState state);
}

/// <summary>
/// ArchiveBot's memory of every cited URL, in Redis beside Hangfire as one hash (archivebot:links).
/// Losing it is safe: every link starts again as unknown, so nothing on a page changes until a link
/// answers or fails three checks again.
/// </summary>
public sealed class RedisLinkStateStore : ILinkStateStore
{
    private const string LinksKey = "archivebot:links";

    private readonly IConnectionMultiplexer _redis;

    public RedisLinkStateStore(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<LinkState?> Get(string url) => LinkState.Decode(await _redis.GetDatabase().HashGetAsync(LinksKey, url));

    public Task Save(string url, LinkState state) => _redis.GetDatabase().HashSetAsync(LinksKey, url, state.Encode());
}

// For tests and the local runner, which have no Redis. Forgets everything when the process ends.
public sealed class InMemoryLinkStateStore : ILinkStateStore
{
    private readonly ConcurrentDictionary<string, LinkState> _states = new();

    public Task<LinkState?> Get(string url) => Task.FromResult(_states.GetValueOrDefault(url));

    public Task Save(string url, LinkState state)
    {
        _states[url] = state;
        return Task.CompletedTask;
    }
}
