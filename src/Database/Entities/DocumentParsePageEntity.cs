using System.ComponentModel.DataAnnotations.Schema;

namespace Doctheca.Database.Entities;

[Table("document_parse_pages")]
public sealed class DocumentParsePageEntity
{
    [Column("parse_id")]
    public Guid ParseId { get; set; }

    [Column("page_number")]
    public int PageNumber { get; set; }

    [Column("label")]
    public string? Label { get; set; }

    [Column("label_source")]
    public string? LabelSource { get; set; }
}
