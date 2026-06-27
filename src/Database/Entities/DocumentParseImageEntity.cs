using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.DocLibrary.Database.Entities;

[Table("document_parse_images")]
public class DocumentParseImageEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("parse_id")]
    [Required]
    public Guid ParseId { get; set; }

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

    [ForeignKey(nameof(ParseId))]
    public DocumentParseEntity? Parse { get; set; }
}
