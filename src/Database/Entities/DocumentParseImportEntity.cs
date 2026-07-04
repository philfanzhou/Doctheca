using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.DocLibrary.Database.Entities;

[Table("document_parse_imports")]
public class DocumentParseImportEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("parse_id")]
    [Required]
    public Guid ParseId { get; set; }

    [Column("imported_by")]
    [Required]
    public Guid ImportedBy { get; set; }

    [Column("status")]
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = string.Empty;

    [Column("note")]
    public string? Note { get; set; }

    [Column("imported_question_ids")]
    public string? ImportedQuestionIds { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [ForeignKey(nameof(ParseId))]
    public DocumentParseEntity? Parse { get; set; }
}
