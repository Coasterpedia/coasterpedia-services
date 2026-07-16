using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WikiClientLibrary;
using WikiClientLibrary.Client;
using WikiClientLibrary.Infrastructures.Logging;

namespace CoasterpediaServices.Common.Wiki;

/// <summary>
/// Provides basic operations for MediaWiki API via HTTP(S).
/// Based off the default implementation of <see cref="WikiClientLibrary.Client.WikiClient"/> in WikiClientLibrary, but simplified and includes a httpClient in the constructor
/// </summary>
public class WikiClient : IWikiClient, IWikiClientLoggable
{
    private readonly HttpClient _httpClient;

    public WikiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    private int _MaxRetries = 3;
    private ILogger _Logger = NullLogger.Instance;

    /// <summary>
    /// Timeout for each query.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Delay before each retry.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Max retries count.
    /// </summary>
    public int MaxRetries
    {
        get { return _MaxRetries; }
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _MaxRetries = value;
        }
    }

    /// <inheritdoc />
    public ILogger Logger
    {
        get => _Logger;
        set => _Logger = value ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public async Task<T> InvokeAsync<T>(string endPointUrl, WikiRequestMessage message,
        IWikiResponseMessageParser<T> responseParser, CancellationToken cancellationToken)
    {
        if (endPointUrl == null) throw new ArgumentNullException(nameof(endPointUrl));
        if (message == null) throw new ArgumentNullException(nameof(message));
        using var scope = this.BeginActionScope(null, message);
        var result = await SendAsync(endPointUrl, message, responseParser, cancellationToken);
        return result;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{GetType().Name}#{RuntimeHelpers.GetHashCode(this)}";
    }

    /// <summary>
    /// Creates an HTTP request message with the given endpoint URL and <see cref="WikiRequestMessage"/> instance.
    /// </summary>
    /// <param name="endpointUrl">MediaWiki API endpoint URL.</param>
    /// <param name="message">The MediaWiki API request message to be sent.</param>
    /// <returns>The actual <see cref="HttpRequestMessage"/> to be sent.</returns>
    /// <remarks>
    /// When overriding this method in derived class, you may change the message headers and/or content after
    /// getting the <see cref="HttpRequestMessage"/> instance from base implementation,
    /// before returning the HTTP request message.
    /// </remarks>
    protected virtual HttpRequestMessage CreateHttpRequestMessage(string endpointUrl, WikiRequestMessage message)
    {
        var url = endpointUrl;
        var query = message.GetHttpQuery();
        if (query != null) url = url + "?" + query;
        return new HttpRequestMessage(message.GetHttpMethod(), url) { Content = message.GetHttpContent() };
    }

    private async Task<T> SendAsync<T>(string endPointUrl, WikiRequestMessage message,
        IWikiResponseMessageParser<T> responseParser, CancellationToken cancellationToken)
    {
        Debug.Assert(endPointUrl != null);
        Debug.Assert(message != null);

        var httpRequest = CreateHttpRequestMessage(endPointUrl, message);
        var retries = 0;

        async Task<bool> PrepareForRetry(TimeSpan delay)
        {
            if (retries >= MaxRetries) return false;
            retries++;
            try
            {
                httpRequest = CreateHttpRequestMessage(endPointUrl, message);
            }
            catch (Exception ex) when (ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                // Some content (e.g. StreamContent with un-seekable Stream) may throw this exception
                // on the second try.
                Logger.LogWarning("Cannot retry: {Exception}.", ex.Message);
                return false;
            }
            Logger.LogDebug("Retry #{Retries} after {Delay}.", retries, RetryDelay);
            await Task.Delay(delay, cancellationToken);
            return true;
        }

        RETRY:
        Logger.LogTrace("Initiate request to: {EndPointUrl}.", endPointUrl);
        cancellationToken.ThrowIfCancellationRequested();
        var requestSw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            // Use await instead of responseTask.Result to unwrap Exceptions.
            // Or AggregateException might be thrown.
            using var responseCancellation = new CancellationTokenSource(Timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, responseCancellation.Token);
            response = await _httpClient.SendAsync(httpRequest, linkedCts.Token);
            // The request has been finished.
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Logger.LogWarning("Cancelled via CancellationToken.");
                cancellationToken.ThrowIfCancellationRequested();
            }
            Logger.LogWarning("Timeout.");
            if (!await PrepareForRetry(RetryDelay)) throw new TimeoutException();
            goto RETRY;
        }
        catch (HttpRequestException ex)
        {
            // SSL protocol not supported. This is ubiquitous on .NET Framework 4.5 but no, not on .NET 5.
            if (ex.InnerException is WebException { Status: WebExceptionStatus.SecureChannelFailure } ex1)
            {
                {
                    throw new HttpRequestException(ex1.Message + "ExceptionSecureChannelFailureHint", ex)
                    {
                        HelpLink = "coasterpedia.net",
                    };
                }
            }
            if (!await PrepareForRetry(RetryDelay)) throw;
            goto RETRY;
        }
        using (response)
        {
            // Validate response.
            var statusCode = (int)response.StatusCode;
            Logger.LogTrace("HTTP {StatusCode}, elapsed: {Time}.", statusCode, requestSw.Elapsed);
            if (!response.IsSuccessStatusCode)
                Logger.LogWarning("HTTP {StatusCode} {Reason}, elapsed {Time}.", statusCode, response.ReasonPhrase, requestSw.Elapsed);
            var localRetryDelay = RetryDelay;
            if (response.Headers.RetryAfter != null)
            {
                Logger.LogWarning("Detected Retry-After header in HTTP response: {RetryAfter}.", response.Headers.RetryAfter);
                if (retries < MaxRetries)
                {
                    // Service Error. We can retry.
                    // HTTP 503 or 200 : https://www.mediawiki.org/wiki/Manual:Maxlag_parameter
                    // Delay per Retry-After Header
                    var date = response.Headers.RetryAfter.Date;
                    var delay = response.Headers.RetryAfter.Delta;
                    if (delay == null && date != null) delay = date - DateTimeOffset.Now;
                    // Or use the default delay
                    if (delay < RetryDelay)
                        localRetryDelay = delay.Value;
                }
            }
            // It's responseParser's turn to check status code.
            cancellationToken.ThrowIfCancellationRequested();
            var context = new WikiResponseParsingContext(Logger, cancellationToken);
            try
            {
                var parsed = await responseParser.ParseResponseAsync(response, context);
                if (context.NeedRetry)
                {
                    if (await PrepareForRetry(localRetryDelay)) goto RETRY;
                    throw new InvalidOperationException("ExceptionWikiClientReachedMaxRetries");
                }
                return (T)parsed;
            }
            catch (Exception ex)
            {
                if (context.NeedRetry && await PrepareForRetry(localRetryDelay))
                {
                    Logger.LogWarning("{Parser}: {Message}", responseParser, ex.Message);
                    goto RETRY;
                }
                Logger.LogWarning(new EventId(), ex, "Parser {Parser} throws an Exception.", responseParser);
                throw;
            }
        }
    }
}