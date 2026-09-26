using System.Text.Json.Serialization;

namespace Doctheca.Service.StructaDoc;

/// <summary>Wire DTO for StructaDoc DocumentResponse (POST/GET /api/v1/documents).</summary>
public sealed class StructaDocDocumentResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("originalFileName")]
    public string? OriginalFileName { get; set; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }

    [JsonPropertyName("extension")]
    public string? Extension { get; set; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("latestParseStatus")]
    public string? LatestParseStatus { get; set; }

    [JsonPropertyName("ownedByCurrentUser")]
    public bool OwnedByCurrentUser { get; set; }
}
