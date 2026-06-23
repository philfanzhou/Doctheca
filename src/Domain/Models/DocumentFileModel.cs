namespace Ruoyu.Study.DocRetrieval.Domain.Models;

public class DocumentFileModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string Status { get; set; } = DocumentFileStatus.Uploaded;
    public string? ExternalTaskId { get; set; }
    public string? MarkdownContent { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? ParsedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
