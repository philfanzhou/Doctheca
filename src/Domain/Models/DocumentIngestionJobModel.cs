using System;

namespace Ruoyu.Study.DocLibrary.Domain.Models;

public class DocumentIngestionJobModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public string Status { get; set; } = DocumentStatus.Pending;
    public string? ParserVersion { get; set; }
    public string? OcrVersion { get; set; }
    public string? ErrorMessage { get; set; }
    public int Progress { get; set; } = 0;
    public string? ProgressStage { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
