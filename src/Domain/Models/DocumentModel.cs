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
    public string Status { get; set; } = DocumentStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
