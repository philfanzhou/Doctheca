using System;

namespace Ruoyu.Study.DocLibrary.Domain.Models;

public class DocumentSegmentModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Guid PageId { get; set; }
    public string SentenceId { get; set; } = string.Empty;
    public string SegmentType { get; set; } = SegmentTypes.Sentence;

    public string Text { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
