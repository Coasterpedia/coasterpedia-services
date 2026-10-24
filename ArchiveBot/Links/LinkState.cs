using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoasterpediaServices.ArchiveBot.Links;

// Stored by name, so reordering this can't change what Redis already holds.
[JsonConverter(typeof(JsonStringEnumConverter<LinkHealth>))]
public enum LinkHealth
{
    // Never answered clearly: only 401/403s, rate limits and timeouts so far.
    Unknown,
    Alive,
    // Failed at least once since it last answered, but not yet often enough to call dead.
    Dying,
    Dead
}

public enum CheckResult
{
    Success,
    Failure,
    Inconclusive
}

public record LinkState(LinkHealth Health, int Failures, DateTimeOffset LastChecked, string? RedirectTarget)
{
    public string Encode() => JsonSerializer.Serialize(this);

    public static LinkState? Decode(string? value) => value == null ? null : JsonSerializer.Deserialize<LinkState>(value);
}

// InternetArchiveBot's rule. A link is dead after three failed checks in a row, and the checks are at
// least three days apart, so one bad afternoon on a server never marks it. One success revives it.
public static class LinkStates
{
    public const int FailuresToDead = 3;
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromDays(3);
    public static readonly TimeSpan AliveRecheckInterval = TimeSpan.FromDays(7);

    public static bool IsDue(LinkState? state, DateTimeOffset now) =>
        state == null || now - state.LastChecked >= (state.Health == LinkHealth.Alive ? AliveRecheckInterval : RecheckInterval);

    public static LinkState Decide(LinkState? previous, CheckResult result, string? redirectTarget, DateTimeOffset now) => result switch
    {
        CheckResult.Success => new LinkState(LinkHealth.Alive, 0, now, redirectTarget),
        CheckResult.Failure when (previous?.Failures ?? 0) + 1 >= FailuresToDead =>
            new LinkState(LinkHealth.Dead, (previous?.Failures ?? 0) + 1, now, null),
        CheckResult.Failure => new LinkState(LinkHealth.Dying, (previous?.Failures ?? 0) + 1, now, null),
        // Says nothing about the page, so keep what we knew. The time still moves on, so a site that
        // blocks us isn't asked again on every edit.
        _ => (previous ?? new LinkState(LinkHealth.Unknown, 0, now, null)) with { LastChecked = now }
    };
}
