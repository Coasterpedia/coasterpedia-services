using System.Text.Json;
using System.Text.Json.Nodes;
using CoasterpediaServices.Projector.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace CoasterpediaServices.Projector.Vocabulary;

/// <summary>
/// Reads each infobox's TemplateData (action=templatedata) for its params'
/// suggestedvalues. That is the list the infobox itself validates against, so
/// a value added on the wiki is valid here at the next refresh with no deploy.
///
/// The one MediaWiki call the Projector makes: a single small request an
/// hour. The last good answer is kept in Redis, so a restart while the wiki
/// is down still projects; with no answer at all, projection stops rather
/// than publish every vocabulary field as null.
/// </summary>
public sealed class VocabularySource
{
    public const string HttpClientName = "projector-wiki";
    private const string CacheKey = "projector:vocabularies";
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConnectionMultiplexer _redis;
    private readonly ProjectorConfig _config;
    private readonly ILogger<VocabularySource> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Vocabularies? _current;
    private DateTimeOffset _fetchedAt;

    public VocabularySource(IHttpClientFactory httpClientFactory, IConnectionMultiplexer redis,
        IOptions<ProjectorConfig> config, ILogger<VocabularySource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _redis = redis;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<Vocabularies> GetAsync(CancellationToken ct = default)
    {
        if (_current != null && DateTimeOffset.UtcNow - _fetchedAt < MaxAge)
        {
            return _current;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (_current != null && DateTimeOffset.UtcNow - _fetchedAt < MaxAge)
            {
                return _current;
            }

            try
            {
                var json = await FetchAsync(ct);
                _current = Parse(json, _config.VocabularyTemplates());
                _fetchedAt = DateTimeOffset.UtcNow;
                await _redis.GetDatabase().StringSetAsync(CacheKey, json.ToJsonString());
                return _current;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "TemplateData fetch failed; using the last good vocabularies");
                if (_current != null)
                {
                    return _current;
                }

                var cached = await _redis.GetDatabase().StringGetAsync(CacheKey);
                if (cached.IsNullOrEmpty)
                {
                    throw new InvalidOperationException("No vocabularies: TemplateData unreachable and nothing cached", ex);
                }

                _current = Parse(JsonNode.Parse(cached.ToString())!, _config.VocabularyTemplates());
                _fetchedAt = DateTimeOffset.UtcNow - MaxAge + TimeSpan.FromMinutes(5);
                return _current;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<JsonNode> FetchAsync(CancellationToken ct)
    {
        var titles = string.Join('|', _config.VocabularyTemplates().Values.SelectMany(t => t).Distinct());
        var url = "w/api.php?action=templatedata&format=json&formatversion=2&redirects=1&titles="
                  + Uri.EscapeDataString(titles);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        await using var stream = await client.GetStreamAsync(url, ct);
        return await JsonSerializer.DeserializeAsync<JsonNode>(stream, cancellationToken: ct)
               ?? throw new InvalidOperationException("Empty TemplateData response");
    }

    /// <param name="templates">Attraction type, or "park" → the templates that store it.</param>
    internal static Vocabularies Parse(JsonNode json, IReadOnlyDictionary<string, string[]> templates)
    {
        var byTitle = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (json["pages"] is JsonObject pages)
        {
            foreach (var (_, page) in pages)
            {
                if (page?["title"]?.GetValue<string>() is { } title && page["params"] is JsonObject ps)
                {
                    byTitle[title] = ps;
                }
            }
        }

        var result = new Dictionary<string, Dictionary<string, List<string>>>();
        foreach (var (type, titles) in templates)
        {
            var byParam = new Dictionary<string, List<string>>();
            foreach (var title in titles)
            {
                if (!byTitle.TryGetValue(title, out var ps))
                {
                    // A missing template would quietly make every value invalid.
                    throw new InvalidOperationException($"TemplateData has no params for {title}");
                }

                foreach (var (param, def) in ps)
                {
                    if (def?["suggestedvalues"] is not JsonArray values)
                    {
                        continue;
                    }

                    if (!byParam.TryGetValue(param, out var list))
                    {
                        byParam[param] = list = [];
                    }

                    foreach (var v in values)
                    {
                        if (v?.GetValue<string>() is { Length: > 0 } s
                            && !list.Contains(s.Trim(), StringComparer.OrdinalIgnoreCase))
                        {
                            list.Add(s.Trim());
                        }
                    }
                }
            }

            result[type] = byParam;
        }

        return new Vocabularies(result);
    }
}
