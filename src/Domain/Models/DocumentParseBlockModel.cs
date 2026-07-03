using System;

namespace Ruoyu.Study.DocLibrary.Domain.Models;

public class DocumentParseBlockModel
{
    public Guid Id { get; set; }
    public Guid ParseId { get; set; }
    public int PageId { get; set; }
    public int SortIndex { get; set; }
    public string BlockType { get; set; } = string.Empty;
    public string? TextContent { get; set; }
    public Guid? ImageId { get; set; }
    public string BlockData { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
