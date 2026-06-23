namespace Ruoyu.Study.DocRetrieval.Domain.Models;

public class DocumentFileImageModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentFileId { get; set; }
    public string ImageName { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = "image/jpeg";
    public long FileSize { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
