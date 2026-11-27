using Doctheca.Ai;

namespace Doctheca.Domain.Models;

/// <summary>
/// Configuration options for LLM document analysis.
/// Inherits common OpenAI-compatible API settings (ApiKey/BaseUrl/Model/TimeoutSeconds/MaxRetries)
/// from ClientOptions. Service-specific fields are defined here.
/// </summary>
public class DocumentAnalysisOptions : ClientOptions
{
    public const string SectionName = "LlmDocumentAnalysis";

    public DocumentAnalysisOptions()
    {
        // Override base defaults: doctheca LLM calls are long-running streaming calls
        // and benefit from a higher retry count and a much longer per-attempt timeout
        // than the typical short VL call.
        MaxRetries = 3;
        TimeoutSeconds = 1800;
    }

    /// <summary>
    /// Model context length in tokens. Supports human-friendly formats: "128K", "1M", "256K".
    /// If set, skips the /models API call. If not set and /models API also fails, LLM document analysis is disabled.
    /// </summary>
    public string ContextLength { get; set; } = string.Empty;

    /// <summary>
    /// Temperature parameter (fixed at 0.1 for structured output)
    /// </summary>
    public double Temperature => 0.1;

    /// <summary>
    /// Idle timeout in seconds while reading an SSE stream. If no SSE event is received
    /// within this window, the call is canceled and retried. Configurable via
    /// LlmDocumentAnalysis__StreamIdleTimeoutSeconds. Default 60s.
    /// This is the primary timeout mechanism for streaming: as long as the LLM keeps
    /// sending tokens, the call stays alive regardless of total elapsed time.
    /// </summary>
    public int StreamIdleTimeoutSeconds { get; set; } = 60;
}
