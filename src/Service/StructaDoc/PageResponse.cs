using System.Text.Json.Serialization;

namespace Doctheca.Service.StructaDoc;

/// <summary>A physical page from the bare-array StructaDoc pages endpoint.</summary>
public sealed class PageResponse
{
    [JsonRequired]
    [JsonPropertyName("number")]
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int Number { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("labelSource")]
    public string? LabelSource { get; set; }
}
