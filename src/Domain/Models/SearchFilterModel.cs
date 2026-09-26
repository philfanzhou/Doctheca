using System;

namespace Doctheca.Domain.Models;

public class SearchFilterModel
{
    // V1 fields (unchanged — zero regression)
    public string? DocumentTitle { get; set; }
    public string? Subject { get; set; }
    public string? Grade { get; set; }
    public string? Year { get; set; }

    // [Gen-2] minerU block-level filters (all nullable; all null → zero regression V1 path)
    /// <summary>Exact match minerU type (maps to index field block_type).</summary>
    public string? BlockType { get; set; }
    /// <summary>Exact match minerU sub_type (maps to index field sub_type).</summary>
    public string? BlockSubType { get; set; }
    /// <summary>Exact match minerU page_id (maps to index field page_number).</summary>
    public int? PageNumber { get; set; }
    /// <summary>Exact match minerU text_level (0=body, 1=h1...; -1 for non-heading text).</summary>
    public int? TextLevel { get; set; }
    /// <summary>Exact match minerU text_format (latex/markdown/none, VLM-only).</summary>
    public string? TextFormat { get; set; }
    /// <summary>Narrow to a specific parse.</summary>
    public Guid? ParseId { get; set; }
    /// <summary>Narrow to a specific document file.</summary>
    public Guid? DocumentFileId { get; set; }
    /// <summary>Filter blocks with img_path (maps to index field has_image).</summary>
    public bool? HasImage { get; set; }
}
