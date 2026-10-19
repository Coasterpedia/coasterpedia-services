using System.Text.Json;
using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Model;
using StackExchange.Redis;

namespace CoasterpediaServices.Projector.State;

/// <summary>
/// The Projector's memory, in Redis beside Hangfire (keys under projector:).
/// Each kind keeps its own records (projector:{kind}:…); trust and the pending
/// edits are per page, shared by every kind. Losing it is safe but noisy: the
/// next sweep finds every record unknown and pushes it again as pending. The
/// settled view keeps the identical content it has, and the runs settle a week
/// later. The established set goes too, so no kind bootstraps on that sweep.
/// </summary>
public sealed class ProjectionState
{
    private const string TrustedKey = "projector:trust:trusted";
    private const string UntrustedKey = "projector:trust:untrusted";
    private const string PendingKey = "projector:pending";
    private const string FlushScheduledKey = "projector:flush-scheduled";
    private const string VocabularyKey = "projector:vocabularies:pushed";
    private const string EstablishedKey = "projector:established";
    public const string OutboxKey = "projector:outbox";

    // Events can arrive out of order; keep the highest revision.
    private const string MaxScript = """
        local current = tonumber(redis.call('HGET', KEYS[1], ARGV[1]) or '0')
        if tonumber(ARGV[2]) > current then redis.call('HSET', KEYS[1], ARGV[1], ARGV[2]) end
        """;

    private readonly IConnectionMultiplexer _redis;

    public ProjectionState(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    private IDatabase Db => _redis.GetDatabase();

    private static string PushedKey(string kind) => $"projector:{kind}:pushed";
    private static string SettlingKey(string kind) => $"projector:{kind}:settling";
    private static string MissingKey(string kind) => $"projector:{kind}:missing";
    private static string IssuesKey(string kind) => $"projector:{kind}:issues";
    private static string MergesKey(string kind) => $"projector:{kind}:merges";

    public async Task<Dictionary<int, RecordState>> GetStatesAsync(string kind, IReadOnlyCollection<int>? ids)
    {
        var result = new Dictionary<int, RecordState>();
        if (ids == null)
        {
            foreach (var e in await Db.HashGetAllAsync(PushedKey(kind)))
            {
                if (RecordState.Decode(e.Value) is { } s)
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

        var values = await Db.HashGetAsync(PushedKey(kind), list.Select(i => (RedisValue)i).ToArray());
        for (var i = 0; i < list.Length; i++)
        {
            if (RecordState.Decode(values[i]) is { } s)
            {
                result[list[i]] = s;
            }
        }

        return result;
    }

    /// <summary>Queue a batch for the edge, then record what it did. The outbox delivers it eventually.</summary>
    public async Task CommitAsync(string kind, string batchJson, IReadOnlyDictionary<int, RecordState> states,
        IReadOnlyCollection<int> deleted)
    {
        await EnqueueAsync(batchJson);
        await SaveAsync(kind, states);

        if (deleted.Count > 0)
        {
            var keys = deleted.Select(d => (RedisValue)d).ToArray();
            await Db.HashDeleteAsync(PushedKey(kind), keys);
            await Db.SortedSetRemoveAsync(SettlingKey(kind), keys);
            await Db.HashDeleteAsync(MissingKey(kind), keys);
            await Db.HashDeleteAsync(TrustedKey, keys);
            await Db.HashDeleteAsync(UntrustedKey, keys);
            await Db.HashDeleteAsync(IssuesKey(kind), keys);
        }
    }

    /// <summary>Queue a batch that records no state: an empty one, carrying only data_as_of.</summary>
    public Task EnqueueAsync(string batchJson) => Db.ListRightPushAsync(OutboxKey, batchJson);

    /// <summary>Record states with nothing to push: an unchanged record taking a newer revision.</summary>
    public async Task SaveAsync(string kind, IReadOnlyDictionary<int, RecordState> states)
    {
        if (states.Count == 0)
        {
            return;
        }

        await Db.HashSetAsync(PushedKey(kind), states.Select(p => new HashEntry(p.Key, p.Value.Encode())).ToArray());

        // The settling index: each pending run, scored by when its clock started.
        var pending = states.Where(s => s.Value.ClockFrom != null)
            .Select(s => new SortedSetEntry(s.Key, s.Value.ClockFrom!.Value.ToUnixTimeSeconds()))
            .ToArray();
        if (pending.Length > 0)
        {
            await Db.SortedSetAddAsync(SettlingKey(kind), pending);
        }

        var settled = states.Where(s => s.Value.ClockFrom == null).Select(s => (RedisValue)s.Key).ToArray();
        if (settled.Length > 0)
        {
            await Db.SortedSetRemoveAsync(SettlingKey(kind), settled);
        }
    }

    /// <summary>Redirect page ID → the record it was last pushed as merging into.</summary>
    public async Task<Dictionary<int, int>> GetMergesAsync(string kind) =>
        (await Db.HashGetAllAsync(MergesKey(kind))).ToDictionary(e => (int)e.Name, e => (int)e.Value);

    /// <summary>Queue a batch of merges, then record them so the next sweep sends only what changed.</summary>
    public async Task CommitMergesAsync(string kind, string batchJson, IReadOnlyCollection<RedirectRow> merges)
    {
        await EnqueueAsync(batchJson);
        await Db.HashSetAsync(MergesKey(kind), merges.Select(m => new HashEntry(m.PageId, m.Target)).ToArray());
    }

    /// <summary>Kinds a sweep has completed for (Settling.IsBootstrap).</summary>
    public async Task<HashSet<string>> GetEstablishedAsync() =>
        (await Db.SetMembersAsync(EstablishedKey)).Select(m => m.ToString()).ToHashSet();

    public Task MarkEstablishedAsync(string kind) => Db.SetAddAsync(EstablishedKey, kind);

    /// <summary>Hash of the vocabulary document last queued for the edge, or null if none was.</summary>
    public async Task<string?> GetPushedVocabularyAsync() => (string?)await Db.StringGetAsync(VocabularyKey);

    public Task SetPushedVocabularyAsync(string hash) => Db.StringSetAsync(VocabularyKey, hash);

    /// <summary>Records whose pending run's clock started at or before <paramref name="cutoff"/>.</summary>
    public async Task<int[]> GetDueAsync(string kind, DateTimeOffset cutoff) =>
        (await Db.SortedSetRangeByScoreAsync(SettlingKey(kind), double.NegativeInfinity, cutoff.ToUnixTimeSeconds()))
        .Select(v => (int)v)
        .ToArray();

    /// <summary>Note an event's revision as trusted or not (DR-010 §6.3): per event, never per job.</summary>
    public void RecordTrust(int pageId, long revision, bool trusted) =>
        Db.ScriptEvaluate(MaxScript, [trusted ? TrustedKey : UntrustedKey], [pageId, revision]);

    public async Task<Dictionary<int, PageTrust>> GetTrustAsync(IReadOnlyCollection<int> ids)
    {
        var result = new Dictionary<int, PageTrust>();
        var list = ids.Distinct().ToArray();
        if (list.Length == 0)
        {
            return result;
        }

        var keys = list.Select(i => (RedisValue)i).ToArray();
        var trusted = await Db.HashGetAsync(TrustedKey, keys);
        var untrusted = await Db.HashGetAsync(UntrustedKey, keys);
        for (var i = 0; i < list.Length; i++)
        {
            result[list[i]] = new PageTrust(
                trusted[i].HasValue ? (long)trusted[i] : 0,
                untrusted[i].HasValue ? (long)untrusted[i] : 0);
        }

        return result;
    }

    /// <summary>One more pass that found the page but no row of this kind. Returns the count so far.</summary>
    public Task<long> IncrementMissingAsync(string kind, int id) => Db.HashIncrementAsync(MissingKey(kind), id);

    public async Task ClearMissingAsync(string kind, IReadOnlyCollection<int> ids)
    {
        if (ids.Count > 0)
        {
            await Db.HashDeleteAsync(MissingKey(kind), ids.Select(i => (RedisValue)i).ToArray());
        }
    }

    /// <summary>Replace the open issues of every page this pass looked at.</summary>
    public async Task SetIssuesAsync(string kind, IReadOnlyCollection<int> looked, IReadOnlyList<DataIssue> issues)
    {
        var byPage = issues.GroupBy(i => i.PageId).ToDictionary(g => g.Key, g => g.ToList());
        var clear = looked.Where(id => !byPage.ContainsKey(id)).Select(id => (RedisValue)id).ToArray();
        if (clear.Length > 0)
        {
            await Db.HashDeleteAsync(IssuesKey(kind), clear);
        }

        if (byPage.Count > 0)
        {
            await Db.HashSetAsync(IssuesKey(kind), byPage
                .Select(g => new HashEntry(g.Key, JsonSerializer.Serialize(g.Value, Wire.Json)))
                .ToArray());
        }
    }

    public async Task<Dictionary<int, string>> GetIssuesAsync(string kind) =>
        (await Db.HashGetAllAsync(IssuesKey(kind))).ToDictionary(e => (int)e.Name, e => e.Value.ToString());

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
