using System;

namespace Ruoyu.Study.DocLibrary.Domain.Models;

public class DocumentParseBlockModel
{
    public Guid Id { get; set; }
    public Guid ParseId { get; set; }
    public int PageId { get; set; }
    public int SortIndex { get; set; }
    public string BlockType { get; set; } = string.Empty;
    public string? TextContent { get; set; }
    public Guid? ImageId { get; set; }
    public string BlockData { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // [Gen-2] minerU block-level structured fields (extracted from block_data JSON in ParseBlock)
    /// <summary>minerU sub_type: secondary classification (caption/body/footnote, e.g. image_caption, table_footnote, code, algorithm, text, ref_text).</summary>
    public string? SubType { get; set; }
    /// <summary>minerU text_level: 0=body, 1=h1, 2=h2...; -1 when field absent (non-heading text).</summary>
    public int TextLevel { get; set; } = -1;
    /// <summary>minerU text_format (VLM-only): latex/markdown/none; empty string for pipeline backend.</summary>
    public string TextFormat { get; set; } = string.Empty;
    /// <summary>minerU bbox x0 (normalized to 0-1000, pipeline convention).</summary>
    public float? BboxX0 { get; set; }
    /// <summary>minerU bbox y0 (normalized to 0-1000, pipeline convention).</summary>
    public float? BboxY0 { get; set; }
    /// <summary>minerU bbox x1 (normalized to 0-1000, pipeline convention).</summary>
    public float? BboxX1 { get; set; }
    /// <summary>minerU bbox y1 (normalized to 0-1000, pipeline convention).</summary>
    public float? BboxY1 { get; set; }
    /// <summary>minerU confidence score (VLM backend; null for pipeline which has no score).</summary>
    public double? MineruScore { get; set; }
    /// <summary>Derived: concatenation of image_caption/table_caption/chart_caption/code_caption texts for keyword recall.</summary>
    public string? Caption { get; set; }
}
