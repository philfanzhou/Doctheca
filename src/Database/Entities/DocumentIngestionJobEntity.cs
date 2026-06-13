using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Database.Entities;

[Table("document_ingestion_jobs")]
public class DocumentIngestionJobEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("document_id")]
    [Required]
    public Guid DocumentId { get; set; }

    [Column("status")]
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = DocumentStatus.Pending;

    [Column("parser_version")]
    [MaxLength(20)]
    public string? ParserVersion { get; set; }

    [Column("ocr_version")]
    [MaxLength(20)]
    public string? OcrVersion { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    [Column("finished_at")]
    public DateTimeOffset? FinishedAt { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [ForeignKey(nameof(DocumentId))]
    public DocumentEntity? Document { get; set; }
}
