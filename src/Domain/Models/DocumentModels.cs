using System;

namespace Ruoyu.Study.DocRetrieval.Domain.Models;

public class DocumentModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Language { get; set; } = "en";
    public string Grade { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Year { get; set; } = string.Empty;
    public string? Tags { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class DocumentPageModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public int PageNumber { get; set; }
    public string? ImagePath { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class DocumentSegmentModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Guid PageId { get; set; }
    public string BlockId { get; set; } = string.Empty;
    public string SentenceId { get; set; } = string.Empty;
    public string SegmentType { get; set; } = "sentence";
    public string Text { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class QuestionSegmentModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Guid PageId { get; set; }
    public string QuestionId { get; set; } = string.Empty;
    public string Stem { get; set; } = string.Empty;
    public string? OptionsJson { get; set; }
    public string? AnswerArea { get; set; }
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class DocumentOccurrenceModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Guid? SegmentId { get; set; }
    public Guid? QuestionSegmentId { get; set; }
    public string TokenText { get; set; } = string.Empty;
    public string TokenStem { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class DocumentIngestionJobModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public string Status { get; set; } = "pending";
    public string? ParserVersion { get; set; }
    public string? OcrVersion { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 文档解析结果
/// </summary>
public class ParsedDocument
{
    public List<ParsedPage> Pages { get; set; } = [];
}

/// <summary>
/// 解析后的页面
/// </summary>
public class ParsedPage
{
    public int PageNumber { get; set; }
    public List<ParsedSegment> Segments { get; set; } = [];
    public List<ParsedQuestion> Questions { get; set; } = [];
}

/// <summary>
/// 解析后的文本片段
/// </summary>
public class ParsedSegment
{
    public string BlockId { get; set; } = string.Empty;
    public string SentenceId { get; set; } = string.Empty;
    public string SegmentType { get; set; } = "sentence";
    public string Text { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public List<ParsedToken> Tokens { get; set; } = [];
}

/// <summary>
/// 解析后的题目
/// </summary>
public class ParsedQuestion
{
    public string QuestionId { get; set; } = string.Empty;
    public string Stem { get; set; } = string.Empty;
    public string? OptionsJson { get; set; }
    public string? AnswerArea { get; set; }
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public List<ParsedToken> Tokens { get; set; } = [];
}

/// <summary>
/// 解析后的 Token（原始文本 + 词干还原）
/// </summary>
public class ParsedToken
{
    public string TokenText { get; set; } = string.Empty;
    public string TokenStem { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
}
