using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.DocRetrieval.Database.Entities;

[Table("document_file_images")]
public class DocumentFileImageEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("document_file_id")]
    [Required]
    public Guid DocumentFileId { get; set; }

    [Column("image_name")]
    [Required]
    [MaxLength(200)]
    public string ImageName { get; set; } = string.Empty;

    [Column("image_path")]
    [Required]
    [MaxLength(500)]
    public string ImagePath { get; set; } = string.Empty;

    [Column("content_type")]
    [Required]
    [MaxLength(50)]
    public string ContentType { get; set; } = "image/jpeg";

    [Column("file_size")]
    public long FileSize { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [ForeignKey(nameof(DocumentFileId))]
    public DocumentFileEntity? DocumentFile { get; set; }
}
