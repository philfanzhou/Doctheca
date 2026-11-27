using System.Text.Json.Serialization;

namespace Doctheca.Ai;

/// <summary>
/// SSE stream chunk. Each "data: {...}" line deserializes to this shape.
/// Only delta.content is accumulated; other fields (finish_reason, usage) are ignored.
/// </summary>
public record StreamChunk
{
    [JsonPropertyName("choices")]
    public List<StreamChoice>? Choices { get; init; }
}

public record StreamChoice
{
    [JsonPropertyName("delta")]
    public StreamDelta? Delta { get; init; }
}

public record StreamDelta
{
    [JsonPropertyName("content")]
    public string? Content { get; init; }
}
