using Ruoyu.Study.Common.Ai;

namespace Ruoyu.Study.DocLibrary.Domain.Models;

/// <summary>
/// Configuration options for LLM document analysis.
/// Inherits common OpenAI-compatible API settings (ApiKey/BaseUrl/Model/TimeoutSeconds/MaxRetries/RetryDelayMs)
/// from AiClientOptions. Service-specific fields (ContextLength, ChunkSize, MaxConcurrency, etc.)
/// are defined here.
/// </summary>
public class DocumentAnalysisOptions : AiClientOptions
{
    public const string SectionName = "LlmDocumentAnalysis";

    public DocumentAnalysisOptions()
    {
        // Override base defaults: doclibrary LLM calls are long-running streaming calls
        // (segmentation of large documents) and benefit from a higher retry count and
        // a much longer per-attempt timeout than the typical short VL call.
        MaxRetries = 3;
        TimeoutSeconds = 1800;
    }

    /// <summary>
    /// Model context length in tokens. Supports human-friendly formats: "128K", "1M", "256K".
    /// If set, skips the /models API call. If not set and /models API also fails, LLM document analysis is disabled.
    /// </summary>
    public string ContextLength { get; set; } = string.Empty;

    /// <summary>
    /// Parsed context length in tokens. Set by InitializeAsync after parsing ContextLength string.
    /// </summary>
    public int ContextLengthTokens { get; set; }

    /// <summary>
    /// Reserved max output tokens for LLM response. Set internally by InitializeAsync.
    /// </summary>
    public int MaxTokensValue { get; set; }

    /// <summary>
    /// Temperature parameter (fixed at 0.1 for structured output)
    /// </summary>
    public double Temperature => 0.1;

    /// <summary>
    /// Text chunk size in characters. Dynamically calculated by InitializeAsync from ContextLength.
    /// </summary>
    public int ChunkSize { get; set; }

    /// <summary>
    /// Idle timeout in seconds while reading an SSE stream. If no SSE event is received
    /// within this window, the call is canceled and retried. Configurable via
    /// LlmDocumentAnalysis__StreamIdleTimeoutSeconds. Default 60s.
    /// This is the primary timeout mechanism for streaming: as long as the LLM keeps
    /// sending tokens, the call stays alive regardless of total elapsed time.
    /// </summary>
    public int StreamIdleTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum number of concurrent LLM calls during chunk segmentation.
    /// Configurable via LlmDocumentAnalysis__MaxConcurrency. Default 2.
    /// Value 1 = fully serial (safest for rate-limited providers).
    /// Value 2-3 = controlled parallelism (overlaps "thinking" time of reasoning models).
    /// Higher values risk provider-side rate limiting.
    /// </summary>
    public int MaxConcurrency { get; set; } = 2;
}
