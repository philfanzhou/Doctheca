using System.Text.Json.Serialization;

namespace Doctheca.Service.StructaDoc;

/// <summary>RFC 7807 problem+json error payload returned by StructaDoc.</summary>
public sealed class StructaDocProblemDetails
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    /// <summary>Machine-readable upload error code extension member.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }
}
