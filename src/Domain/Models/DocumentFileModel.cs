namespace Doctheca.Domain.Models;

public class DocumentFileModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public Guid? StructaDocDocumentId { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public Guid? CreatedBy { get; set; }
    public string? Subject { get; set; }
    public string? Grade { get; set; }
    public string? Year { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
