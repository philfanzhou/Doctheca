using System.Text.Json.Serialization;

namespace Doctheca.Service.StructaDoc;

/// <summary>Wire DTO for the paginated blocks response ({ items, nextSequence }).</summary>
public sealed class BlockPageResponse
{
    [JsonPropertyName("items")]
    public List<BlockResponse> Items { get; set; } = [];

    /// <summary>Sequence cursor of the last item; null means this was the final page.</summary>
    [JsonPropertyName("nextSequence")]
    public int? NextSequence { get; set; }
}
