using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Doctheca.Database.Entities;

/// <summary>
/// One configured level of a structure definition; its name is administrator data. A null
/// <see cref="ParentLevelId"/> marks a top level; the parent always belongs to the same definition.
/// </summary>
[Table("structure_levels")]
public class StructureLevelEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("definition_id")]
    [Required]
    public Guid DefinitionId { get; set; }

    [Column("name")]
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Column("parent_level_id")]
    public Guid? ParentLevelId { get; set; }

    [Column("sort_order")]
    [Required]
    public int SortOrder { get; set; }

    [ForeignKey(nameof(DefinitionId))]
    public StructureDefinitionEntity? Definition { get; set; }

    [ForeignKey(nameof(ParentLevelId))]
    public StructureLevelEntity? ParentLevel { get; set; }
}
