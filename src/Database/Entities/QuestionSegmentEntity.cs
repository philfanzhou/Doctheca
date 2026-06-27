using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.DocLibrary.Database.Entities;

[Table("question_segments")]
public class QuestionSegmentEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("document_id")]
    [Required]
    public Guid DocumentId { get; set; }

    [Column("page_id")]
    [Required]
    public Guid PageId { get; set; }

    [Column("question_id")]
    [Required]
    [MaxLength(50)]
    public string QuestionId { get; set; } = string.Empty;

    [Column("stem")]
    [Required]
    public string Stem { get; set; } = string.Empty;

    [Column("options_json")]
    public string? OptionsJson { get; set; }

    [Column("answer_area")]
    public string? AnswerArea { get; set; }

    [Column("start_offset")]
    public int StartOffset { get; set; }

    [Column("end_offset")]
    public int EndOffset { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [ForeignKey(nameof(DocumentId))]
    public DocumentEntity? Document { get; set; }

    [ForeignKey(nameof(PageId))]
    public DocumentPageEntity? Page { get; set; }
}
