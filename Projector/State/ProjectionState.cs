using System.Text.Json;
using CoasterpediaServices.Projector.Model;
using StackExchange.Redis;

namespace CoasterpediaServices.Projector.State;

/// <summary>What the Projector last pushed for one record.</summary>
public sealed record PushedState(string Hash, long Revision, DateTimeOffset UpdatedAt)
{
    public string Encode() => $"{Hash}|{Revision}|{UpdatedAt.ToUnixTimeSeconds()}";

    public static PushedState? Decode(string? value)
    {
        var parts = value?.Split('|');
        return parts is { Length: 3 }
            ? new PushedState(parts[0], long.Parse(parts[1]), DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[2])))
            : null;
    }
}

/// <summary>
/// The Projector's memory, in Redis beside Hangfire (keys under projector:).
/// Losing it is safe: the next sweep sees every record as changed and pushes
/// the lot, which the edge applies idempotently.
/// </summary>
public sealed class ProjectionState
{
    private const string PushedKey = "projector:attraction:pushed";
    private const string MissingKey = "projector:attraction:missing";
    private const string IssuesKey = "projector:issues";
    private const string PendingKey = "projector:pending";
    private const string FlushScheduledKey = "projector:flush-scheduled";
    public const string OutboxKey = "projector:outbox";

    private readonly IConnectionMultiplexer _redis;

    public ProjectionState(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    private IDatabase Db => _redis.GetDatabase();

    public async Task<Dictionary<int, PushedState>> GetPushedAsync(IReadOnlyCollection<int>? ids)
    {
        var result = new Dictionary<int, PushedState>();
        if (ids == null)
        {
            foreach (var e in await Db.HashGetAllAsync(PushedKey))
            {
                if (PushedState.Decode(e.Value) is { } s)
                {
                    result[(int)e.Name] = s;
                }
            }

            return result;
        }

        var list = ids.Distinct().ToArray();
        if (list.Length == 0)
        {
            return result;
        }

        var values = await Db.HashGetAsync(PushedKey, list.Select(i => (RedisValue)i).ToArray());
        for (var i = 0; i < list.Length; i++)
        {
            if (PushedState.Decode(values[i]) is { } s)
            {
                result[list[i]] = s;
            }
        }

        return result;
    }

    /// <summary>Queue a batch for the edge, then record it as pushed. The outbox delivers it eventually.</summary>
    public async Task CommitAsync(string batchJson, IReadOnlyDictionary<int, PushedState> pushed, IReadOnlyCollection<int> deleted)
    {
        await Db.ListRightPushAsync(OutboxKey, batchJson);
        if (pushed.Count > 0)
        {
            await Db.HashSetAsync(PushedKey, pushed.Select(p => new HashEntry(p.Key, p.Value.Encode())).ToArray());
        }

        if (deleted.Count > 0)
        {
            var keys = deleted.Select(d => (RedisValue)d).ToArray();
            await Db.HashDeleteAsync(PushedKey, keys);
            await Db.HashDeleteAsync(MissingKey, keys);
            await Db.HashDeleteAsync(IssuesKey, keys);
        }
    }

    /// <summary>One more pass that found the page but no attraction row. Returns the count so far.</summary>
    public Task<long> IncrementMissingAsync(int id) => Db.HashIncrementAsync(MissingKey, id);

    public async Task ClearMissingAsync(IReadOnlyCollection<int> ids)
    {
        if (ids.Count > 0)
        {
            await Db.HashDeleteAsync(MissingKey, ids.Select(i => (RedisValue)i).ToArray());
        }
    }

    /// <summary>Replace the open issues of every page this pass looked at.</summary>
    public async Task SetIssuesAsync(IReadOnlyCollection<int> looked, IReadOnlyList<DataIssue> issues)
    {
        var byPage = issues.GroupBy(i => i.PageId).ToDictionary(g => g.Key, g => g.ToList());
        var clear = looked.Where(id => !byPage.ContainsKey(id)).Select(id => (RedisValue)id).ToArray();
        if (clear.Length > 0)
        {
            await Db.HashDeleteAsync(IssuesKey, clear);
        }

        if (byPage.Count > 0)
        {
            await Db.HashSetAsync(IssuesKey, byPage
                .Select(g => new HashEntry(g.Key, JsonSerializer.Serialize(g.Value, Wire.Json)))
                .ToArray());
        }
    }

    public async Task<Dictionary<int, string>> GetIssuesAsync() =>
        (await Db.HashGetAllAsync(IssuesKey)).ToDictionary(e => (int)e.Name, e => e.Value.ToString());

    /// <summary>Note an edited page. True when no flush is scheduled yet, so the caller schedules one.</summary>
    public bool AddPending(int pageId, TimeSpan debounce)
    {
        Db.SetAdd(PendingKey, pageId);
        return Db.StringSet(FlushScheduledKey, 1, debounce + TimeSpan.FromMinutes(5), When.NotExists);
    }

    /// <summary>Take every pending page. Clears the schedule flag first, so a later edit schedules its own flush.</summary>
    public async Task<int[]> TakePendingAsync()
    {
        await Db.KeyDeleteAsync(FlushScheduledKey);
        var members = await Db.SetMembersAsync(PendingKey);
        if (members.Length > 0)
        {
            await Db.SetRemoveAsync(PendingKey, members);
        }

        return members.Select(m => (int)m).ToArray();
    }

    public Task<RedisValue> PeekOutboxAsync() => Db.ListGetByIndexAsync(OutboxKey, 0);

    public Task PopOutboxAsync() => Db.ListLeftPopAsync(OutboxKey);

    public Task<long> OutboxLengthAsync() => Db.ListLengthAsync(OutboxKey);
}
