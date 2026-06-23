namespace Ruoyu.Study.DocRetrieval.Domain.Models;

public class DocumentParseImageModel
{
    public Guid Id { get; set; }
    public Guid ParseId { get; set; }
    public string ImageName { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = "image/jpeg";
}
