using System;

namespace Ruoyu.Study.DocRetrieval.Domain.Models;

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
