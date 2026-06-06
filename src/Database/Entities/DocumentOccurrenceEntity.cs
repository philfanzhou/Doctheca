using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.DocRetrieval.Database.Entities;

[Table("document_occurrences")]
public class DocumentOccurrenceEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("document_id")]
    [Required]
    public Guid DocumentId { get; set; }

    [Column("segment_id")]
    public Guid? SegmentId { get; set; }

    [Column("question_segment_id")]
    public Guid? QuestionSegmentId { get; set; }

    [Column("token_text")]
    [Required]
    [MaxLength(200)]
    public string TokenText { get; set; } = string.Empty;

    [Column("token_stem")]
    [Required]
    [MaxLength(200)]
    public string TokenStem { get; set; } = string.Empty;

    [Column("start_offset")]
    public int StartOffset { get; set; }

    [Column("end_offset")]
    public int EndOffset { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [ForeignKey(nameof(DocumentId))]
    public DocumentEntity? Document { get; set; }

    [ForeignKey(nameof(SegmentId))]
    public DocumentSegmentEntity? Segment { get; set; }

    [ForeignKey(nameof(QuestionSegmentId))]
    public QuestionSegmentEntity? QuestionSegment { get; set; }
}
