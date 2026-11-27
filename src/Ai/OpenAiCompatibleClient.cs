using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Doctheca.Ai;

/// <summary>
/// Client for the SSE streaming mode of an OpenAI-compatible chat/completions API.
/// Handles Bearer token auth, per-attempt timeout and retry with linear backoff.
///
/// Each instance is bound to a specific ClientOptions (one client per service configuration).
/// HttpClient is provided by IHttpClientFactory / AddHttpClient&lt;T&gt; for lifecycle management.
/// </summary>
public class OpenAiCompatibleClient
{
    private readonly HttpClient _httpClient;
    private readonly ClientOptions _options;
    private readonly ILogger _logger;
    private readonly SseReader _sseReader;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Exposes the underlying HttpClient for callers that need to hit other endpoints
    /// on the same OpenAI-compatible API (e.g. GET /models/{id} for metadata queries).
    /// BaseAddress and Authorization are already configured by the constructor.
    /// </summary>
    public HttpClient HttpClient => _httpClient;

    /// <summary>
    /// Streaming use (e.g. doctheca LLM document analysis).
    /// HttpClient.Timeout is set to InfiniteTimeSpan so streaming responses are not
    /// prematurely canceled; per-attempt timeout and idle timeout are enforced via
    /// CancellationTokenSource and SseReader respectively.
    /// </summary>
    public OpenAiCompatibleClient(HttpClient httpClient, ClientOptions options, ILogger logger, int streamIdleTimeoutSeconds)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
        _sseReader = new SseReader(logger, streamIdleTimeoutSeconds);

        // Configure HttpClient - BaseUrl must end with / for relative path resolution
        var baseUrl = options.BaseUrl.TrimEnd('/');
        httpClient.BaseAddress = new Uri(baseUrl + "/");

        // HttpClient.Timeout must be disabled for streaming, otherwise long-running
        // streams get canceled by the absolute timeout before tokens finish arriving.
        httpClient.Timeout = Timeout.InfiniteTimeSpan;

        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }

    /// <summary>
    /// SSE streaming call: returns accumulated delta.content as a string.
    /// Used for long-text generation scenarios (e.g. LLM document analysis).
    /// </summary>
    public async Task<string> CallStreamingAsync(object requestBody, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            try
            {
                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
                {
                    Content = JsonContent.Create(requestBody, options: JsonOptions),
                };
                // PostAsJsonAsync buffers the full response, which defeats streaming.
                // Use SendAsync with ResponseHeadersRead so we can read the SSE body
                // incrementally as tokens arrive.
                using var response = await _httpClient.SendAsync(
                    requestMessage,
                    HttpCompletionOption.ResponseHeadersRead,
                    attemptCts.Token);
                response.EnsureSuccessStatusCode();

                var content = await _sseReader.ReadAllAsync(response.Content, attemptCts.Token);

                if (string.IsNullOrWhiteSpace(content))
                {
                    throw new InvalidOperationException("API returned empty streaming response");
                }

                _logger.LogInformation("API streaming call completed in {ElapsedMs}ms, output length={Length}",
                    sw.ElapsedMilliseconds, content.Length);
                return content;
            }
            catch (Exception ex) when (attempt < _options.MaxRetries && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "API streaming call attempt {Attempt}/{MaxRetries} failed after {ElapsedMs}ms, retrying",
                    attempt, _options.MaxRetries, sw.ElapsedMilliseconds);
                await Task.Delay(TimeSpan.FromSeconds(1 * attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException("API streaming call failed after all retries");
    }
}
