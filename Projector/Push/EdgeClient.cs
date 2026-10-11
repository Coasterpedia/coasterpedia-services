using System.Net.Http.Headers;
using System.Text;
using CoasterpediaServices.Projector.Options;
using CoasterpediaServices.Projector.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoasterpediaServices.Projector.Push;

/// <summary>
/// Delivers the outbox to the edge's /internal/ingest, oldest batch first.
/// A batch leaves the outbox only once the edge accepts it; when Cloudflare
/// cannot be reached the batches wait and drain later (DR-010 §4). Every
/// batch is idempotent, so a batch sent twice does no harm.
/// </summary>
public sealed class EdgeClient
{
    public const string HttpClientName = "projector-edge";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ProjectionState _state;
    private readonly ProjectorConfig _config;
    private readonly ILogger<EdgeClient> _logger;

    public EdgeClient(IHttpClientFactory httpClientFactory, ProjectionState state, IOptions<ProjectorConfig> config,
        ILogger<EdgeClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _state = state;
        _config = config.Value;
        _logger = logger;
    }

    /// <returns>Batches delivered.</returns>
    public async Task<int> DrainAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.IngestUrl))
        {
            _logger.LogWarning("No IngestUrl; {Count} batches wait in the outbox", await _state.OutboxLengthAsync());
            return 0;
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var sent = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = await _state.PeekOutboxAsync();
            if (batch.IsNullOrEmpty)
            {
                break;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, _config.IngestUrl)
            {
                Content = new StringContent(batch.ToString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
            request.Headers.Add("CF-Access-Client-Id", _config.AccessClientId);
            request.Headers.Add("CF-Access-Client-Secret", _config.AccessClientSecret);
            if (_config.DevIngestToken.Length > 0)
            {
                request.Headers.Add("X-Dev-Ingest-Token", _config.DevIngestToken);
            }

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Edge unreachable; {Count} batches wait", await _state.OutboxLengthAsync());
                break;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                // A 4xx means the edge rejects this batch and will keep doing so. It stays
                // at the head, blocking the rest, until someone looks: skipping it would
                // leave the edge silently missing records.
                _logger.LogError("Edge refused a batch: {Status} {Body}", (int)response.StatusCode, body);
                break;
            }

            await _state.PopOutboxAsync();
            sent++;
        }

        return sent;
    }
}
