using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Doctheca.Database.Entities;

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

    // [Gen-2] minerU block-level structured columns (part of the migration baseline)
    [Column("sub_type")]
    [MaxLength(50)]
    public string? SubType { get; set; }

    [Column("text_level")]
    public int TextLevel { get; set; } = -1;

    [Column("text_format")]
    [MaxLength(20)]
    public string TextFormat { get; set; } = string.Empty;

    [Column("bbox_x0")]
    public float? BboxX0 { get; set; }

    [Column("bbox_y0")]
    public float? BboxY0 { get; set; }

    [Column("bbox_x1")]
    public float? BboxX1 { get; set; }

    [Column("bbox_y1")]
    public float? BboxY1 { get; set; }

    [Column("score")]
    public double? MineruScore { get; set; }

    [Column("caption")]
    public string? Caption { get; set; }

    [ForeignKey(nameof(ParseId))]
    public DocumentParseEntity? Parse { get; set; }

    [ForeignKey(nameof(ImageId))]
    public DocumentParseImageEntity? Image { get; set; }
}
