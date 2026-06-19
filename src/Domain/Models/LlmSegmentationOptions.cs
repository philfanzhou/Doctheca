namespace Ruoyu.Study.DocRetrieval.Domain.Models;

/// <summary>
/// Configuration options for LLM segmentation service
/// </summary>
public class LlmSegmentationOptions
{
    public const string SectionName = "LlmSegmentation";

    /// <summary>
    /// LLM provider: openai, anthropic, local
    /// </summary>
    public string Provider { get; set; } = "openai";

    /// <summary>
    /// Model name: gpt-4o-mini, claude-haiku, local-model, etc.
    /// </summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// API key for the LLM provider
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Base URL for the LLM API
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>
    /// Maximum tokens for LLM response
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// Temperature parameter (0.0 = deterministic, 1.0 = creative)
    /// </summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>
    /// Text chunk size in characters for segmentation
    /// </summary>
    public int ChunkSize { get; set; } = 2500;

    /// <summary>
    /// Maximum retry attempts for LLM calls
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Timeout in seconds for LLM API calls
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;
}
