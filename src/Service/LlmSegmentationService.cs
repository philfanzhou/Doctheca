using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;

namespace Ruoyu.Study.DocLibrary.Service;

/// <summary>
/// LLM-based intelligent document segmentation service implementation.
/// Uses OpenAI-compatible API for document analysis and text segmentation.
/// Dynamically fetches model capabilities at startup to optimize parameters.
/// </summary>
public class LlmSegmentationService : ILlmSegmentationService
{
    private readonly HttpClient _httpClient;
    private readonly LlmSegmentationOptions _options;
    private readonly ILogger<LlmSegmentationService> _logger;
    private bool _initialized;

    public int ChunkSize => _options.ChunkSize;
    public int MaxConcurrency => _options.MaxConcurrency;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public LlmSegmentationService(
        HttpClient httpClient,
        IOptions<LlmSegmentationOptions> options,
        ILogger<LlmSegmentationService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        // Configure HttpClient - BaseUrl must end with / for relative path resolution
        var baseUrl = _options.BaseUrl.TrimEnd('/');
        _httpClient.BaseAddress = new Uri(baseUrl + "/");
        // Disable HttpClient.Timeout so streaming responses are not prematurely canceled.
        // Per-attempt timeout is enforced via CancellationTokenSource in CallLlmAsync.
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
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
            var maxTokens = ParseTokenCount(_options.MaxTokens);

            // If ContextLength not configured, try fetching from API
            if (contextLength <= 0)
            {
                contextLength = await TryFetchContextLengthAsync(cancellationToken);
            }

            _options.ContextLengthTokens = contextLength;
            _options.MaxTokensValue = maxTokens > 0 ? maxTokens : 4096;

            if (contextLength > 0)
            {
                // ChunkSize: use 80% of remaining context for input (20% safety margin)
                var availableInputTokens = contextLength - 200 - _options.MaxTokensValue;
                var safeInputTokens = (int)(availableInputTokens * 0.8);
                var calculatedChunkSize = (int)(safeInputTokens * 1.5);

                // Cap ChunkSize to keep individual LLM calls fast and avoid timeouts.
                // More calls with smaller chunks > fewer calls that timeout.
                // 2500 cap controls output volume for high-output strategies (e.g. word_entry
                // produces ~80 chars JSON per entry → ~70 entries per 2500-char chunk).
                const int maxChunkSize = 2_500;
                _options.ChunkSize = Math.Min(calculatedChunkSize, maxChunkSize);

                _logger.LogInformation(
                    "LLM model initialized: Model={Model}, ContextLength={ContextLength}, MaxTokens={MaxTokens}, ChunkSize={ChunkSize}",
                    _options.Model, contextLength, _options.MaxTokensValue, _options.ChunkSize);
            }
            else
            {
                // No context length available — disable LLM segmentation
                _options.MaxTokensValue = 0;
                _options.ChunkSize = 0;

                _logger.LogWarning(
                    "LLM segmentation disabled: ContextLength not configured and model info unavailable. " +
                    "Set LlmSegmentation__ContextLength in config to enable. Model={Model}",
                    _options.Model);
            }

            _initialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM initialization failed, segmentation disabled");
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
    /// </summary>
    private async Task<int> TryFetchContextLengthAsync(CancellationToken cancellationToken)
    {
        // Approach 1: GET /v1/models/{model}
        try
        {
            var response = await _httpClient.GetAsync($"models/{_options.Model}", cancellationToken);
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

    public async Task<DocumentProfile> AnalyzeDocumentAsync(string textPreview, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(textPreview))
        {
            _logger.LogWarning("Empty text preview provided for document analysis");
            return new DocumentProfile();
        }

        try
        {
            // Truncate to first 2000 chars for analysis
            var preview = textPreview.Length > 2000 ? textPreview[..2000] : textPreview;

            var prompt = BuildAnalysisPrompt(preview);
            var response = await CallLlmAsync(prompt, cancellationToken);
            return ParseDocumentProfile(response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM document analysis failed, returning default profile");
            return new DocumentProfile();
        }
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
            var response = await CallLlmAsync(prompt, cancellationToken);
            return ParseMetadataAnalysis(response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM metadata analysis failed, returning null");
            return null;
        }
    }

    public async Task<List<SegmentResult>> SegmentTextAsync(string text, DocumentProfile profile, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var prompt = BuildSegmentationPrompt(text, profile);
        var response = await CallLlmAsync(prompt, cancellationToken);
        return ParseSegmentResults(response, text);
    }

    public async Task<DocumentProfile> RefineProfileAsync(
        string textPreview,
        DocumentProfile originalProfile,
        List<SegmentCorrection> corrections,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(textPreview) || corrections.Count == 0)
        {
            return originalProfile;
        }

        try
        {
            var preview = textPreview.Length > 2000 ? textPreview[..2000] : textPreview;
            var prompt = BuildRefinementAnalysisPrompt(preview, originalProfile, corrections);
            var response = await CallLlmAsync(prompt, cancellationToken);
            return ParseDocumentProfile(response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM profile refinement failed, returning original profile");
            return originalProfile;
        }
    }

    public async Task<List<SegmentResult>> RefineSegmentTextAsync(
        string text,
        DocumentProfile profile,
        List<SegmentCorrection> corrections,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var prompt = BuildRefinementSegmentationPrompt(text, profile, corrections);
        var response = await CallLlmAsync(prompt, cancellationToken);
        return ParseSegmentResults(response, text);
    }

    #region Prompt Building

    private static string BuildAnalysisPrompt(string textPreview)
    {
        return $$"""
            你是一个文档分析专家。分析以下文本片段，识别文档的学科、年级、年份、类型和结构特征。

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
            - 无法判断时返回空字符串

            年份（从标题、页眉、版权页等推断，格式为4位数字如"2024"、"2025"）：
            - 无法判断时返回空字符串

            文档类型：
            - 教材：正式教学材料，包含章节、知识点讲解
            - 知识点过关单：知识点列表，通常有编号
            - 单词表：英文单词+中文释义的列表
            - 短语表：英文短语+中文释义的列表
            - 试卷：包含题号、选项的考试材料
            - 其他：无法明确判断

            分段策略：
            - sentence：按完整句子分段（适合教材、阅读材料）
            - concept：按概念/知识点分段（适合知识点过关单、理科教材）
            - word_entry：按词条分段（适合单词表、短语表）
            - question：按题目分段（适合试卷）
            - knowledge_point：按知识点分段（适合理科教材）

            请分析以下文本并返回 JSON 格式的文档画像。只返回 JSON，不要有其他文字。

            ```json
            {
              "subject": "学科",
              "grade": "年级",
              "year": "年份",
              "doc_type": "文档类型",
              "segment_strategy": "分段策略",
              "structure": {
                "has_chapters": true/false,
                "has_questions": true/false,
                "has_word_list": true/false,
                "has_formulas": true/false
              }
            }
            ```

            文本片段：
            ---
            {{textPreview}}
            ---
            """;
    }

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

    private static string BuildSegmentationPrompt(string text, DocumentProfile profile)
    {
        var strategyHint = profile.SegmentStrategy switch
        {
            SegmentTypes.WordEntry => "每个词条（单词+释义）为一个segment。",
            SegmentTypes.Question => "每道题（题干+选项）为一个segment。",
            SegmentTypes.Concept => "每个概念/公式为一个segment。",
            SegmentTypes.KnowledgePoint => "每个知识点为一个segment。",
            _ => "每个完整句子为一个segment。"
        };

        return $$"""
            按 {{profile.SegmentStrategy}} 策略分段。{{strategyHint}}
            返回JSON，segments按原文顺序排列，保留原文。不需要计算offset。

            {"segments":[{"text":"...","segment_type":"{{profile.SegmentStrategy}}"}]}

            ---
            {{text}}
            ---
            """;
    }

    private static string BuildRefinementAnalysisPrompt(string textPreview, DocumentProfile originalProfile, List<SegmentCorrection> corrections)
    {
        var examplesBuilder = new StringBuilder();
        for (var i = 0; i < corrections.Count; i++)
        {
            var c = corrections[i];
            examplesBuilder.AppendLine($"示例 {i + 1}:");
            examplesBuilder.AppendLine($"  操作: {c.Action}");
            examplesBuilder.AppendLine($"  原始 IDs: [{string.Join(", ", c.OriginalSentenceIds)}]");
            if (c.Action == "merge" && c.NewText != null)
                examplesBuilder.AppendLine($"  合并后文本: \"{c.NewText}\"");
            if (c.Action == "split" && c.SplitPosition.HasValue)
                examplesBuilder.AppendLine($"  拆分位置: {c.SplitPosition}");
            if (c.NewSegmentType != null)
                examplesBuilder.AppendLine($"  目标类型: {c.NewSegmentType}");
            examplesBuilder.AppendLine();
        }

        return $$"""
            你是一个文档分析专家。用户对文档的分段结果进行了修正，请根据修正示例重新分析文档的学科、类型和分段策略。

            用户修正示例：
            {{examplesBuilder}}

            原始分析结果：学科={{originalProfile.Subject}}，类型={{originalProfile.DocType}}，策略={{originalProfile.SegmentStrategy}}

            请根据用户的修正意图，重新分析文档画像。只返回 JSON，不要有其他文字。

            ```json
            {
              "subject": "学科",
              "doc_type": "文档类型",
              "segment_strategy": "分段策略",
              "structure": {
                "has_chapters": true/false,
                "has_questions": true/false,
                "has_word_list": true/false,
                "has_formulas": true/false
              }
            }
            ```

            文本片段：
            ---
            {{textPreview}}
            ---
            """;
    }

    private static string BuildRefinementSegmentationPrompt(string text, DocumentProfile profile, List<SegmentCorrection> corrections)
    {
        var examplesBuilder = new StringBuilder();
        for (var i = 0; i < corrections.Count; i++)
        {
            var c = corrections[i];
            examplesBuilder.AppendLine($"示例 {i + 1}:");
            examplesBuilder.AppendLine($"  操作: {c.Action}");
            if (c.Action == "merge" && c.NewText != null)
            {
                examplesBuilder.AppendLine($"  正确分段: [{{\"text\": \"{EscapeJson(c.NewText)}\", \"segment_type\": \"{c.NewSegmentType ?? profile.SegmentStrategy}\"}}]");
            }
            else if (c.Action == "split" && c.SplitPosition.HasValue)
            {
                examplesBuilder.AppendLine($"  在位置 {c.SplitPosition} 处拆分为两段");
            }
            else if (c.Action == "retype" && c.NewSegmentType != null)
            {
                examplesBuilder.AppendLine($"  类型应改为: {c.NewSegmentType}");
            }
            examplesBuilder.AppendLine();
        }

        var strategyDescription = profile.SegmentStrategy switch
        {
            SegmentTypes.Sentence => "按完整句子分段。",
            SegmentTypes.Concept => "按知识点/概念分段。",
            SegmentTypes.WordEntry => "按词条分段。",
            SegmentTypes.Question => "按题目分段。",
            SegmentTypes.KnowledgePoint => "按知识点分段。",
            _ => "按完整句子分段。"
        };

        return $$"""
            你是一个文档分段专家。用户对之前的分段结果进行了修正，请参照修正示例重新分段。

            分段规则：{{strategyDescription}}

            用户修正示例（请严格按照这些示例的风格和类型选择来分段）：
            {{examplesBuilder}}

            请将以下文本分段，返回 JSON 格式。只返回 JSON，不要有其他文字。不需要计算offset。

            ```json
            {
              "segments": [
                {"text": "第一段文本", "segment_type": "{{profile.SegmentStrategy}}"}
              ]
            }
            ```

            文本：
            ---
            {{text}}
            ---
            """;
    }

    private static string EscapeJson(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }

    #endregion

    #region LLM API Calls

    private async Task<string> CallLlmAsync(string prompt, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Per-attempt timeout: covers request send + streaming read. HttpClient.Timeout
            // is disabled (InfiniteTimeSpan) so streaming tokens don't trigger an absolute
            // timeout. Link with caller's token so external cancellation still works.
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            try
            {
                var request = new
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

                // PostAsJsonAsync buffers the full response, which defeats streaming.
                // Use SendAsync with ResponseHeadersRead so we can read the SSE body
                // incrementally as tokens arrive.
                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
                {
                    Content = JsonContent.Create(request, options: JsonOptions),
                };
                using var response = await _httpClient.SendAsync(
                    requestMessage,
                    HttpCompletionOption.ResponseHeadersRead,
                    attemptCts.Token);
                response.EnsureSuccessStatusCode();

                var content = await ReadSseStreamAsync(response.Content, attemptCts.Token);

                if (string.IsNullOrWhiteSpace(content))
                {
                    throw new InvalidOperationException("LLM returned empty response");
                }

                _logger.LogInformation("LLM call completed in {ElapsedMs}ms (streaming), output length={Length}",
                    sw.ElapsedMilliseconds, content.Length);
                return content;
            }
            catch (Exception ex) when (attempt < _options.MaxRetries && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "LLM call attempt {Attempt}/{MaxRetries} failed after {ElapsedMs}ms, retrying",
                    attempt, _options.MaxRetries, sw.ElapsedMilliseconds);
                await Task.Delay(TimeSpan.FromSeconds(1 * attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException("LLM call failed after all retries");
    }

    /// <summary>
    /// Read an OpenAI-compatible SSE stream and accumulate delta.content into a single string.
    /// Each event line has the form "data: {json}". The stream ends with "data: [DONE]".
    ///
    /// Uses a per-read idle timeout: every ReadLineAsync gets its own linked token that
    /// cancels if no SSE event arrives within StreamIdleTimeoutSeconds. As long as the
    /// provider keeps sending tokens, the overall call can take much longer than the
    /// hard total timeout.
    /// </summary>
    private async Task<string> ReadSseStreamAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var contentBuilder = new StringBuilder();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var idleTimeout = TimeSpan.FromSeconds(_options.StreamIdleTimeoutSeconds);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? line;
            using (var lineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                lineCts.CancelAfter(idleTimeout);
                try
                {
                    line = await reader.ReadLineAsync(lineCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"No SSE data received for {_options.StreamIdleTimeoutSeconds}s (idle timeout).");
                }
            }

            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            StreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<StreamChunk>(data, JsonOptions);
            }
            catch (JsonException ex)
            {
                // Skip malformed chunks (e.g. keep-alive comments) but log for diagnostics
                _logger.LogDebug(ex, "Skipping malformed SSE chunk: {Data}", data);
                continue;
            }

            var delta = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
            if (!string.IsNullOrEmpty(delta))
            {
                contentBuilder.Append(delta);
            }
        }

        return contentBuilder.ToString();
    }

    #endregion

    #region Response Parsing

    private DocumentProfile ParseDocumentProfile(string response)
    {
        try
        {
            // Extract JSON from response (may be wrapped in markdown code block)
            var json = ExtractJson(response);
            var parsed = JsonSerializer.Deserialize<AnalysisResponse>(json, JsonOptions);

            if (parsed == null)
            {
                _logger.LogWarning("Failed to parse LLM analysis response, using defaults");
                return new DocumentProfile();
            }

            var strategy = parsed.SegmentStrategy?.ToLowerInvariant() switch
            {
                "sentence" => SegmentTypes.Sentence,
                "concept" => SegmentTypes.Concept,
                "word_entry" => SegmentTypes.WordEntry,
                "question" => SegmentTypes.Question,
                "knowledge_point" => SegmentTypes.KnowledgePoint,
                _ => SegmentTypes.Sentence
            };

            return new DocumentProfile
            {
                Subject = parsed.Subject ?? "其他",
                Grade = parsed.Grade ?? string.Empty,
                Year = parsed.Year ?? string.Empty,
                DocType = parsed.DocType ?? "其他",
                SegmentStrategy = strategy,
                Structure = new DocumentStructure
                {
                    HasChapters = parsed.Structure?.HasChapters ?? false,
                    HasQuestions = parsed.Structure?.HasQuestions ?? false,
                    HasWordList = parsed.Structure?.HasWordList ?? false,
                    HasFormulas = parsed.Structure?.HasFormulas ?? false
                }
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse LLM analysis response as JSON, using defaults");
            return new DocumentProfile();
        }
    }

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

    private List<SegmentResult> ParseSegmentResults(string response, string originalText)
    {
        try
        {
            var json = ExtractJson(response);
            var parsed = JsonSerializer.Deserialize<SegmentationResponse>(json, JsonOptions);

            if (parsed?.Segments == null || parsed.Segments.Count == 0)
            {
                _logger.LogWarning("LLM returned empty segments, falling back");
                return [];
            }

            // Compute offsets from ordered text using IndexOf.
            // We no longer ask the LLM to calculate offsets — it's error-prone and
            // wastes thinking time for reasoning models. Instead, we locate each
            // segment's text in the original string sequentially.
            var results = new List<SegmentResult>();
            var searchFrom = 0;

            foreach (var seg in parsed.Segments)
            {
                if (string.IsNullOrWhiteSpace(seg.Text))
                    continue;

                var startOffset = originalText.IndexOf(seg.Text, searchFrom, StringComparison.Ordinal);
                if (startOffset < 0)
                {
                    // Text not found — skip this segment rather than producing bad offsets
                    _logger.LogDebug("Segment text not found in original, skipping: {Text}...",
                        seg.Text.Length > 40 ? seg.Text[..40] + "..." : seg.Text);
                    continue;
                }

                var endOffset = startOffset + seg.Text.Length;
                searchFrom = endOffset;

                results.Add(new SegmentResult
                {
                    Text = seg.Text,
                    StartOffset = startOffset,
                    EndOffset = endOffset,
                    SegmentType = seg.SegmentType ?? SegmentTypes.Sentence
                });
            }

            return results;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse LLM segmentation response as JSON");
            return [];
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
    /// SSE stream chunk. Each "data: {...}" line deserializes to this shape.
    /// Only delta.content is accumulated; other fields (finish_reason, usage) are ignored.
    /// </summary>
    private record StreamChunk
    {
        [JsonPropertyName("choices")]
        public List<StreamChoice>? Choices { get; init; }
    }

    private record StreamChoice
    {
        [JsonPropertyName("delta")]
        public StreamDelta? Delta { get; init; }
    }

    private record StreamDelta
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }

    private record AnalysisResponse
    {
        [JsonPropertyName("subject")]
        public string? Subject { get; init; }

        [JsonPropertyName("grade")]
        public string? Grade { get; init; }

        [JsonPropertyName("year")]
        public string? Year { get; init; }

        [JsonPropertyName("doc_type")]
        public string? DocType { get; init; }

        [JsonPropertyName("segment_strategy")]
        public string? SegmentStrategy { get; init; }

        [JsonPropertyName("structure")]
        public StructureResponse? Structure { get; init; }
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

    private record StructureResponse
    {
        [JsonPropertyName("has_chapters")]
        public bool HasChapters { get; init; }

        [JsonPropertyName("has_questions")]
        public bool HasQuestions { get; init; }

        [JsonPropertyName("has_word_list")]
        public bool HasWordList { get; init; }

        [JsonPropertyName("has_formulas")]
        public bool HasFormulas { get; init; }
    }

    private record SegmentationResponse
    {
        [JsonPropertyName("segments")]
        public List<SegmentResponse>? Segments { get; init; }
    }

    private record SegmentResponse
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }

        // Offset fields kept for backward compatibility: LLM may still return them,
        // but we ignore them and compute offsets ourselves via IndexOf.
        [JsonPropertyName("start_offset")]
        public int StartOffset { get; init; }

        [JsonPropertyName("end_offset")]
        public int EndOffset { get; init; }

        [JsonPropertyName("segment_type")]
        public string? SegmentType { get; init; }
    }

    #endregion
}
