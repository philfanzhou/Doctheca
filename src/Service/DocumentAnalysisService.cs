using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Doctheca.Ai;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;

namespace Doctheca.Service;

/// <summary>
/// LLM-based document metadata analysis service implementation.
/// Uses OpenAI-compatible API (via shared OpenAiCompatibleClient) to analyze
/// document subject/grade/year metadata when MinerU parse completes and metadata is missing.
/// Dynamically fetches model capabilities at startup to optimize parameters.
/// </summary>
public class DocumentAnalysisService : IDocumentAnalysisService
{
    private readonly OpenAiCompatibleClient _client;
    private readonly DocumentAnalysisOptions _options;
    private readonly ILogger<DocumentAnalysisService> _logger;
    private bool _initialized;
    private int _contextLengthTokens;
    private int _maxTokensValue;
    private int _chunkSize;

    public int ChunkSize => _chunkSize;
    public int MaxConcurrency => _options.MaxConcurrency;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public DocumentAnalysisService(
        OpenAiCompatibleClient client,
        IOptions<DocumentAnalysisOptions> options,
        ILogger<DocumentAnalysisService> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Initialize model parameters by fetching model info from API.
    /// Called once at startup.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        var contextLength = 0;
        var maxTokensValue = 0;
        var chunkSize = 0;

        try
        {
            contextLength = ParseTokenCount(_options.ContextLength);
            // If ContextLength not configured, try fetching from API
            if (contextLength <= 0)
            {
                contextLength = await TryFetchContextLengthAsync(cancellationToken);
            }

            maxTokensValue = 4096;

            if (contextLength > 0)
            {
                // ChunkSize: use 80% of remaining context for input (20% safety margin)
                var availableInputTokens = contextLength - 200 - maxTokensValue;
                var safeInputTokens = (int)(availableInputTokens * 0.8);
                var calculatedChunkSize = (int)(safeInputTokens * 1.5);

                // Cap ChunkSize to keep individual LLM calls fast and avoid timeouts.
                const int maxChunkSize = 2_500;
                chunkSize = Math.Min(calculatedChunkSize, maxChunkSize);

                _logger.LogInformation(
                    "LLM document analysis initialized: Model={Model}, ContextLength={ContextLength}, ReservedOutputTokens={MaxTokens}, ChunkSize={ChunkSize}",
                    _options.Model, contextLength, maxTokensValue, chunkSize);
            }
            else
            {
                // No context length available — disable LLM document analysis
                maxTokensValue = 0;
                chunkSize = 0;

                _logger.LogWarning(
                    "LLM document analysis disabled: ContextLength not configured and model info unavailable. " +
                    "Set LlmDocumentAnalysis__ContextLength in config to enable. Model={Model}",
                    _options.Model);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM document analysis initialization failed");
            maxTokensValue = 0;
            chunkSize = 0;
        }
        finally
        {
            _contextLengthTokens = contextLength;
            _maxTokensValue = maxTokensValue;
            _chunkSize = chunkSize;
            _initialized = true;
        }
    }

    /// <summary>
    /// Parse human-friendly token count string to integer.
    /// Supports: "128K", "1M", "256K", "4K", "131072" (raw number).
    /// </summary>
    internal static int ParseTokenCount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;

        value = value.Trim().ToUpperInvariant();

        if (value.EndsWith('K'))
        {
            if (int.TryParse(value[..^1], out var k))
                return k * 1024;
            return 0;
        }

        if (value.EndsWith('M'))
        {
            if (int.TryParse(value[..^1], out var m))
                return m * 1024 * 1024;
            return 0;
        }

        if (int.TryParse(value, out var raw))
            return raw;

        return 0;
    }

    /// <summary>
    /// Try to fetch model context length from API. Returns 0 if unavailable.
    /// Uses OpenAiCompatibleClient.HttpClient so BaseAddress + Authorization are reused.
    /// </summary>
    private async Task<int> TryFetchContextLengthAsync(CancellationToken cancellationToken)
    {
        // Approach 1: GET /v1/models/{model}
        try
        {
            var response = await _client.HttpClient.GetAsync($"models/{_options.Model}", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var modelInfo = await response.Content.ReadFromJsonAsync<DocumentAnalysisResponseParser.ModelInfoResponse>(JsonOptions, cancellationToken);
                if (modelInfo?.ContextLength > 0)
                {
                    _logger.LogInformation("Model info fetched from /models/{Model}: ContextLength={ContextLength}",
                        _options.Model, modelInfo.ContextLength);
                    return modelInfo.ContextLength;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GET /models/{Model} failed", _options.Model);
        }

        return 0;
    }

    public async Task<DocumentMetadataAnalysis?> AnalyzeMetadataAsync(string textPreview, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(textPreview))
        {
            _logger.LogWarning("Empty text preview provided for metadata analysis");
            return null;
        }

        if (_chunkSize <= 0 || _maxTokensValue <= 0)
        {
            _logger.LogInformation("LLM metadata analysis skipped: LLM not initialized or disabled");
            return null;
        }

        try
        {
            // Truncate to first 2000 chars — enough for title/subject/grade identification
            var preview = textPreview.Length > 2000 ? textPreview[..2000] : textPreview;

            var prompt = DocumentAnalysisPromptBuilder.BuildMetadataAnalysisPrompt(preview);
            var response = await _client.CallStreamingAsync(BuildRequestBody(prompt), cancellationToken);
            return DocumentAnalysisResponseParser.ParseMetadataAnalysis(response, _logger, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM metadata analysis failed, returning null");
            return null;
        }
    }

    private object BuildRequestBody(string prompt)
    {
        return new
        {
            model = _options.Model,
            messages = new[]
            {
                new { role = "system", content = "直接返回JSON，不要解释。" },
                new { role = "user", content = prompt }
            },
            max_tokens = _maxTokensValue,
            temperature = _options.Temperature,
            // Enable SSE streaming so tokens flow as they're generated, keeping the
            // connection active and avoiding idle-timeout on long LLM generations.
            stream = true
        };
    }
}
