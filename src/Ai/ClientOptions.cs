namespace Doctheca.Ai;

/// <summary>
/// Base configuration options for OpenAI-compatible API clients.
/// Each service inherits and extends with its own business-specific fields.
/// </summary>
public abstract class ClientOptions
{
    /// <summary>
    /// API key for the LLM/VL provider (required to enable the service).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Base URL for the API (e.g. https://api.xiaomimimo.com/v1).
    /// Trailing slash is optional; client normalizes it.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Model ID (e.g. mimo-v2.5-pro, Qwen/Qwen3-VL-30B-A3B-Instruct).
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Hard total timeout in seconds for a single API attempt (request send + response read).
    /// Enforced via CancellationTokenSource per attempt; the streaming client disables
    /// HttpClient.Timeout and relies on this value and its idle timeout instead.
    /// Default: 60s.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum retry attempts (in addition to the initial call).
    /// Default: 2 (total 3 attempts).
    /// </summary>
    public int MaxRetries { get; set; } = 2;
}
