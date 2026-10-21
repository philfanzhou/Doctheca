using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Doctheca.Database.Entities;

/// <summary>
/// A hand-marked structure node of one parse: a contiguous, inclusive range of block
/// <c>sequence</c> values at one configured level. Deleting the parse deletes its nodes.
/// </summary>
[Table("document_structure_nodes")]
public class DocumentStructureNodeEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("parse_id")]
    [Required]
    public Guid ParseId { get; set; }

    [Column("level_id")]
    [Required]
    public Guid LevelId { get; set; }

    [Column("parent_id")]
    public Guid? ParentId { get; set; }

    [Column("name")]
    [Required]
    [MaxLength(500)]
    public string Name { get; set; } = string.Empty;

    [Column("sort_order")]
    [Required]
    public int SortOrder { get; set; }

    [Column("start_sequence")]
    [Required]
    public int StartSequence { get; set; }

    [Column("end_sequence")]
    [Required]
    public int EndSequence { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [ForeignKey(nameof(ParseId))]
    public DocumentParseEntity? Parse { get; set; }

    [ForeignKey(nameof(LevelId))]
    public StructureLevelEntity? Level { get; set; }

    [ForeignKey(nameof(ParentId))]
    public DocumentStructureNodeEntity? Parent { get; set; }
}
