using System;

namespace Ruoyu.Study.DocLibrary.Domain.Models;

public class DocumentPageModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public int PageNumber { get; set; }
    public string? ImagePath { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
