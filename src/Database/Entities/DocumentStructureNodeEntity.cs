using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Doctheca.Database.Entities;

/// <summary>
/// A document-owned chapter with an optional same-document inclusive content binding.
/// Parse deletion clears the binding and retains the chapter.
/// </summary>
[Table("document_structure_nodes")]
public class DocumentStructureNodeEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("document_file_id")] public Guid DocumentFileId { get; set; }
    [Column("tree_id")] public Guid TreeId { get; set; }

    [Column("parse_id")]
    public Guid? ParseId { get; set; }

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
    public int? StartSequence { get; set; }

    [Column("end_sequence")]
    public int? EndSequence { get; set; }

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
