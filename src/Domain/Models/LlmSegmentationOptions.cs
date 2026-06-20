namespace Ruoyu.Study.DocRetrieval.Domain.Models;

/// <summary>
/// Configuration options for LLM segmentation service.
/// Only ApiKey, BaseUrl, and Model need to be configured by user.
/// Other parameters are dynamically calculated based on model capabilities.
/// </summary>
public class LlmSegmentationOptions
{
    public const string SectionName = "LlmSegmentation";

    /// <summary>
    /// API key for the LLM provider (required)
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Base URL for the LLM API (required)
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Model ID (required)
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Model context length in tokens. Supports human-friendly formats: "128K", "1M", "256K".
    /// If set, skips the /models API call. If not set and /models API also fails, LLM segmentation is disabled.
    /// </summary>
    public string ContextLength { get; set; } = string.Empty;

    /// <summary>
    /// Parsed context length in tokens. Set by InitializeAsync after parsing ContextLength string.
    /// </summary>
    public int ContextLengthTokens { get; set; }

    /// <summary>
    /// Maximum tokens for LLM response. Configurable via LlmSegmentation__MaxTokens.
    /// Default 4096. Check your model's documentation for the actual limit.
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// Temperature parameter (fixed at 0.1 for structured output)
    /// </summary>
    public double Temperature => 0.1;

    /// <summary>
    /// Text chunk size in characters (dynamically calculated at startup)
    /// </summary>
    public int ChunkSize { get; set; } = 1500;

    /// <summary>
    /// Maximum retry attempts (fixed at 3)
    /// </summary>
    public int MaxRetries => 3;

    /// <summary>
    /// Timeout in seconds for LLM API calls. Configurable via LlmSegmentation__TimeoutSeconds.
    /// Default 300s. Increase for large documents or slow models.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;
}
