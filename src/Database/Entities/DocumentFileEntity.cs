using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.DocRetrieval.Database.Entities;

[Table("document_files")]
public class DocumentFileEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("file_name")]
    [Required]
    [MaxLength(500)]
    public string FileName { get; set; } = string.Empty;

    [Column("file_path")]
    [Required]
    [MaxLength(500)]
    public string FilePath { get; set; } = string.Empty;

    [Column("file_size")]
    public long FileSize { get; set; }

    [Column("content_type")]
    [Required]
    [MaxLength(100)]
    public string ContentType { get; set; } = string.Empty;

    [Column("status")]
    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = "uploaded";

    [Column("external_task_id")]
    [MaxLength(100)]
    public string? ExternalTaskId { get; set; }

    [Column("markdown_content")]
    public string? MarkdownContent { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("parsed_at")]
    public DateTimeOffset? ParsedAt { get; set; }

    [Column("created_by")]
    public Guid? CreatedBy { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    [ConcurrencyCheck]
    public DateTimeOffset? UpdatedAt { get; set; }
}
