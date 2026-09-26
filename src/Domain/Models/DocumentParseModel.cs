namespace Doctheca.Domain.Models;

public class DocumentParseModel
{
    public Guid Id { get; set; }
    public Guid DocumentFileId { get; set; }
    public string ModelVersion { get; set; } = "vlm";
    public string Status { get; set; } = DocumentParseStatus.Pending;
    public string? ExternalTaskId { get; set; }
    public Guid? StructaDocParseRunId { get; set; }
    public string? MarkdownContent { get; set; }
    public string? ContentList { get; set; }
    public string? ContentListV2 { get; set; }
    public string? ModelJson { get; set; }
    public string? LayoutJson { get; set; }
    public string? ZipPath { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? ParsedAt { get; set; }
}
