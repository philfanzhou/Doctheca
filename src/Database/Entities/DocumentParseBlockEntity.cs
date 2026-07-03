using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.DocLibrary.Database.Entities;

[Table("document_parse_blocks")]
public class DocumentParseBlockEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("parse_id")]
    [Required]
    public Guid ParseId { get; set; }

    [Column("page_id")]
    [Required]
    public int PageId { get; set; }

    [Column("sort_index")]
    [Required]
    public int SortIndex { get; set; }

    [Column("block_type")]
    [Required]
    [MaxLength(20)]
    public string BlockType { get; set; } = string.Empty;

    [Column("text_content")]
    public string? TextContent { get; set; }

    [Column("image_id")]
    public Guid? ImageId { get; set; }

    [Column("block_data")]
    [Required]
    public string BlockData { get; set; } = "{}";

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [ForeignKey(nameof(ParseId))]
    public DocumentParseEntity? Parse { get; set; }

    [ForeignKey(nameof(ImageId))]
    public DocumentParseImageEntity? Image { get; set; }
}
