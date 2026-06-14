using System;

namespace Ruoyu.Study.DocRetrieval.Domain.Models;

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
