using System;
using System.Collections.Generic;

namespace Ruoyu.Study.DocRetrieval.Domain.Models;

/// <summary>
/// User-submitted correction for a segment
/// </summary>
public record SegmentCorrection
{
    /// <summary>
    /// Original sentence IDs affected by this correction
    /// </summary>
    public List<string> OriginalSentenceIds { get; init; } = [];

    /// <summary>
    /// Action type: merge, split, retype
    /// </summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>
    /// New text after merge or split (for merge: concatenated text; for split: not used)
    /// </summary>
    public string? NewText { get; init; }

    /// <summary>
    /// Character position where to split (for split action only)
    /// </summary>
    public int? SplitPosition { get; init; }

    /// <summary>
    /// New segment type (for retype action, or override for merge/split)
    /// </summary>
    public string? NewSegmentType { get; init; }
}

/// <summary>
/// DTO for segment data returned to admin UI
/// </summary>
public record SegmentDto
{
    public Guid Id { get; init; }
    public string SentenceId { get; init; } = string.Empty;
    public string SegmentType { get; init; } = SegmentTypes.Sentence;
    public string Text { get; init; } = string.Empty;
    public int StartOffset { get; init; }
    public int EndOffset { get; init; }
    public int PageNumber { get; init; }
}

/// <summary>
/// DTO for document segments response
/// </summary>
public record DocumentSegmentsDto
{
    public Guid DocumentId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DocumentProfile? Profile { get; init; }
    public List<SegmentDto> Segments { get; init; } = [];
    public int TotalCount { get; init; }
}

/// <summary>
/// Result of a refinement operation
/// </summary>
public record RefinementResult
{
    public Guid DocumentId { get; init; }
    public Guid BackupId { get; init; }
    public int CorrectionCount { get; init; }
    public string Message { get; init; } = string.Empty;
    public string BackupDataJson { get; init; } = string.Empty;
}

/// <summary>
/// Backup data structure stored as JSON
/// </summary>
public record SegmentBackupData
{
    public List<SegmentDto> Segments { get; init; } = [];
    public string? ProfileJson { get; init; }
}
