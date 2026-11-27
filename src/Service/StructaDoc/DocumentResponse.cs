using System.Text.Json.Serialization;

namespace Doctheca.Service.StructaDoc;

/// <summary>Wire DTO for StructaDoc DocumentResponse (POST/GET /api/v1/documents).</summary>
public sealed class DocumentResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }
}
