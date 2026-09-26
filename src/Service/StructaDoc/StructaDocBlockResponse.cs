using System.Text.Json.Serialization;

namespace Doctheca.Service.StructaDoc;

/// <summary>Wire DTO for a StructaDoc ParseBlockResponse item.</summary>
public sealed class StructaDocBlockResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>0-based global reading-order sequence.</summary>
    [JsonPropertyName("sequence")]
    public int Sequence { get; set; }

    /// <summary>1-based page number; null when the source has no reliable physical pages.</summary>
    [JsonPropertyName("pageNumber")]
    public int? PageNumber { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("subtype")]
    public string? Subtype { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("contentFormat")]
    public string? ContentFormat { get; set; }

    [JsonPropertyName("boundingBox")]
    public StructaDocBoundingBox? BoundingBox { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    /// <summary>Asset ID within the same Parse Run; bytes via the asset content endpoint.</summary>
    [JsonPropertyName("assetId")]
    public Guid? AssetId { get; set; }
}

/// <summary>Normalized 0-1 bounding box, origin top-left.</summary>
public sealed class StructaDocBoundingBox
{
    [JsonPropertyName("x0")]
    public double X0 { get; set; }

    [JsonPropertyName("y0")]
    public double Y0 { get; set; }

    [JsonPropertyName("x1")]
    public double X1 { get; set; }

    [JsonPropertyName("y1")]
    public double Y1 { get; set; }
}
