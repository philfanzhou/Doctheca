using System.Text.Json.Serialization;

namespace Doctheca.Service.StructaDoc;

/// <summary>Wire DTO for StructaDoc ParseRunResponse.</summary>
public sealed class StructaDocParseRunResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("documentId")]
    public Guid DocumentId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("stage")]
    public string? Stage { get; set; }

    [JsonPropertyName("providerType")]
    public string? ProviderType { get; set; }

    [JsonPropertyName("providerConfigId")]
    public Guid ProviderConfigId { get; set; }

    [JsonPropertyName("providerConfigVersionId")]
    public Guid ProviderConfigVersionId { get; set; }

    [JsonPropertyName("sourceMediaType")]
    public string? SourceMediaType { get; set; }

    [JsonPropertyName("submittedMediaType")]
    public string? SubmittedMediaType { get; set; }

    [JsonPropertyName("attemptCount")]
    public int AttemptCount { get; set; }

    [JsonPropertyName("maxAttempts")]
    public int MaxAttempts { get; set; }

    [JsonPropertyName("nextAttemptAt")]
    public DateTimeOffset? NextAttemptAt { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; set; }
}
