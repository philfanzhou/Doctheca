using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.DocRetrieval.Database.Entities;

[Table("document_segments")]
public class DocumentSegmentEntity
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

    [Column("block_id")]
    [Required]
    [MaxLength(50)]
    public string BlockId { get; set; } = string.Empty;

    [Column("sentence_id")]
    [Required]
    [MaxLength(50)]
    public string SentenceId { get; set; } = string.Empty;

    [Column("segment_type")]
    [Required]
    [MaxLength(20)]
    public string SegmentType { get; set; } = "sentence";

    [Column("text")]
    [Required]
    public string Text { get; set; } = string.Empty;

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
