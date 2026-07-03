namespace Ruoyu.Study.DocLibrary.Domain.Models;

public class DocumentParseModel
{
    public Guid Id { get; set; }
    public Guid DocumentFileId { get; set; }
    public string Status { get; set; } = DocumentParseStatus.Pending;
    public string? ExternalTaskId { get; set; }
    public string? MarkdownContent { get; set; }
    public string? ContentList { get; set; }
    public string? ZipPath { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? ParsedAt { get; set; }
}
