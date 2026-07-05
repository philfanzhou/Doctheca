using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ruoyu.Study.Common.Ai;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;

namespace Ruoyu.Study.DocLibrary.Service;

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

    public int ChunkSize => _options.ChunkSize;
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

        try
        {
            var contextLength = ParseTokenCount(_options.ContextLength);
            // If ContextLength not configured, try fetching from API
            if (contextLength <= 0)
            {
                contextLength = await TryFetchContextLengthAsync(cancellationToken);
            }

            _options.ContextLengthTokens = contextLength;
            _options.MaxTokensValue = 4096;

            if (contextLength > 0)
            {
                // ChunkSize: use 80% of remaining context for input (20% safety margin)
                var availableInputTokens = contextLength - 200 - _options.MaxTokensValue;
                var safeInputTokens = (int)(availableInputTokens * 0.8);
                var calculatedChunkSize = (int)(safeInputTokens * 1.5);

                // Cap ChunkSize to keep individual LLM calls fast and avoid timeouts.
                const int maxChunkSize = 2_500;
                _options.ChunkSize = Math.Min(calculatedChunkSize, maxChunkSize);

                _logger.LogInformation(
                    "LLM document analysis initialized: Model={Model}, ContextLength={ContextLength}, ReservedOutputTokens={MaxTokens}, ChunkSize={ChunkSize}",
                    _options.Model, contextLength, _options.MaxTokensValue, _options.ChunkSize);
            }
            else
            {
                // No context length available — disable LLM document analysis
                _options.MaxTokensValue = 0;
                _options.ChunkSize = 0;

                _logger.LogWarning(
                    "LLM document analysis disabled: ContextLength not configured and model info unavailable. " +
                    "Set LlmDocumentAnalysis__ContextLength in config to enable. Model={Model}",
                    _options.Model);
            }

            _initialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM document analysis initialization failed");
            _options.MaxTokensValue = 0;
            _options.ChunkSize = 0;
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
                var modelInfo = await response.Content.ReadFromJsonAsync<ModelInfoResponse>(JsonOptions, cancellationToken);
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

        if (_options.ChunkSize <= 0 || _options.MaxTokensValue <= 0)
        {
            _logger.LogInformation("LLM metadata analysis skipped: LLM not initialized or disabled");
            return null;
        }

        try
        {
            // Truncate to first 2000 chars — enough for title/subject/grade identification
            var preview = textPreview.Length > 2000 ? textPreview[..2000] : textPreview;

            var prompt = BuildMetadataAnalysisPrompt(preview);
            var response = await _client.CallStreamingAsync(BuildRequestBody(prompt), cancellationToken);
            return ParseMetadataAnalysis(response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM metadata analysis failed, returning null");
            return null;
        }
    }

    #region Prompt Building

    /// <summary>
    /// Build a focused prompt for metadata-only analysis (subject, grade, year).
    /// Internal for unit testing.
    /// </summary>
    internal static string BuildMetadataAnalysisPrompt(string textPreview)
    {
        return $$"""
            你是一个文档分析专家。分析以下文档内容，识别学科和年级。

            学科类型：
            - English：英语教材、阅读材料
            - 语文：语文教材、文言文、现代文
            - 数学：数学教材、习题集
            - 物理：物理教材、实验报告
            - 化学：化学教材、实验报告
            - 生物：生物教材
            - 其他：无法明确判断

            年级（从标题或内容推断）：
            - K：幼儿园/学前
            - G1-G12：小学一年级到高三
            - 无法判断时返回 null

            年份（从标题、页眉、版权页等推断，4位数字）：
            - 无法判断时返回 null

            请分析以下文档内容并返回 JSON。只返回 JSON，不要有其他文字。

            ```json
            {
              "subject": "学科或null",
              "grade": "年级或null",
              "year": "年份或null"
            }
            ```

            文档内容：
            ---
            {{textPreview}}
            ---
            """;
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
            max_tokens = _options.MaxTokensValue,
            temperature = _options.Temperature,
            // Enable SSE streaming so tokens flow as they're generated, keeping the
            // connection active and avoiding idle-timeout on long LLM generations.
            stream = true
        };
    }

    #endregion

    #region Response Parsing

    /// <summary>
    /// Parse LLM metadata analysis response into DocumentMetadataAnalysis.
    /// Returns null on parse failure. Internal for unit testing.
    /// </summary>
    internal DocumentMetadataAnalysis? ParseMetadataAnalysis(string response)
    {
        try
        {
            var json = ExtractJson(response);
            var parsed = JsonSerializer.Deserialize<MetadataAnalysisResponse>(json, JsonOptions);

            if (parsed == null)
            {
                _logger.LogWarning("Failed to parse LLM metadata analysis response");
                return null;
            }

            // Treat empty strings as null (LLM may return "" instead of null)
            var subject = string.IsNullOrWhiteSpace(parsed.Subject) ? null : parsed.Subject.Trim();
            var grade = string.IsNullOrWhiteSpace(parsed.Grade) ? null : parsed.Grade.Trim();
            var year = string.IsNullOrWhiteSpace(parsed.Year) ? null : parsed.Year.Trim();

            // Skip "null" string literal that some LLMs return instead of JSON null
            subject = subject == "null" ? null : subject;
            grade = grade == "null" ? null : grade;
            year = year == "null" ? null : year;

            return new DocumentMetadataAnalysis
            {
                Subject = subject,
                Grade = grade,
                Year = year
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse LLM metadata analysis response as JSON");
            return null;
        }
    }

    /// <summary>
    /// Extract JSON from response, handling markdown code blocks
    /// </summary>
    private static string ExtractJson(string response)
    {
        var trimmed = response.Trim();

        // Remove markdown code block wrapper if present
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            var start = trimmed.IndexOf('\n');
            var end = trimmed.LastIndexOf("```");
            if (start >= 0 && end > start)
            {
                trimmed = trimmed[(start + 1)..end].Trim();
            }
        }
        else if (trimmed.StartsWith("```"))
        {
            var start = trimmed.IndexOf('\n');
            var end = trimmed.LastIndexOf("```");
            if (start >= 0 && end > start)
            {
                trimmed = trimmed[(start + 1)..end].Trim();
            }
        }

        return trimmed;
    }

    #endregion

    #region API Response Models

    private record ModelInfoResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("context_length")]
        public int ContextLength { get; init; }
    }

    /// <summary>
    /// LLM response for metadata-only analysis (subject, grade, year).
    /// All fields nullable — LLM returns null when it cannot determine the value.
    /// </summary>
    private record MetadataAnalysisResponse
    {
        [JsonPropertyName("subject")]
        public string? Subject { get; init; }

        [JsonPropertyName("grade")]
        public string? Grade { get; init; }

        [JsonPropertyName("year")]
        public string? Year { get; init; }
    }

    #endregion
}
