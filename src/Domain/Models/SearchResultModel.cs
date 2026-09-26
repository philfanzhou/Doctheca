namespace Doctheca.Domain.Models;

public class SearchResultModel
{
    // V1 fields (unchanged — zero regression)
    public string DocumentName { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public string AssociatedText { get; set; } = string.Empty;
    public double Score { get; set; }
    public string MatchType { get; set; } = string.Empty;
    public string SegmentId { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }

    // [Gen-2] minerU block-level fields (optional, null when absent — backward compatible)
    /// <summary>minerU original block JSON raw string (from _source._meta.block_data, for detail display).</summary>
    public string? BlockData { get; set; }
    /// <summary>minerU bbox [x0, y0, x1, y1] normalized to 0-1000 (pipeline convention).</summary>
    public float[]? Bbox { get; set; }
    /// <summary>minerU confidence score (VLM backend; distinct from V1 Score which is OpenSearch _score).</summary>
    public double? MineruScore { get; set; }
    /// <summary>minerU sub_type: secondary classification (caption/body/footnote).</summary>
    public string? SubType { get; set; }
    /// <summary>minerU text_level: 0=body, 1=h1...; -1 for non-heading text.</summary>
    public int? TextLevel { get; set; }
    /// <summary>minerU text_format (VLM-only): latex/markdown/none.</summary>
    public string? TextFormat { get; set; }
    /// <summary>Derived caption text (concatenated image/table/chart/code captions for keyword recall).</summary>
    public string? Caption { get; set; }
}
