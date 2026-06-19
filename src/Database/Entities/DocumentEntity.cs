using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Database.Entities;

[Table("documents")]
public class DocumentEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("title")]
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Column("source_type")]
    [Required]
    [MaxLength(20)]
    public string SourceType { get; set; } = string.Empty;

    [Column("file_hash")]
    [Required]
    [MaxLength(64)]
    public string FileHash { get; set; } = string.Empty;

    [Column("file_path")]
    [Required]
    [MaxLength(500)]
    public string FilePath { get; set; } = string.Empty;

    [Column("file_size")]
    public long FileSize { get; set; }

    [Column("language")]
    [Required]
    [MaxLength(10)]
    public string Language { get; set; } = "en";

    [Column("grade")]
    [Required]
    [MaxLength(20)]
    public string Grade { get; set; } = string.Empty;

    [Column("subject")]
    [Required]
    [MaxLength(20)]
    public string Subject { get; set; } = string.Empty;

    [Column("year")]
    [Required]
    [MaxLength(10)]
    public string Year { get; set; } = string.Empty;

    [Column("tags")]
    public string? Tags { get; set; }

    [Column("status")]
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = DocumentStatus.Pending;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    [ConcurrencyCheck]
    public DateTimeOffset? UpdatedAt { get; set; }

    [Column("llm_profile_json")]
    public string? LlmProfileJson { get; set; }
}
