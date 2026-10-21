using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Doctheca.Database.Entities;

/// <summary>
/// A runtime-configured kind of document structure (for example "English textbook"). Its levels
/// are rows in <c>structure_levels</c>; no level name is known to the code.
/// </summary>
[Table("structure_definitions")]
public class StructureDefinitionEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("name")]
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    public List<StructureLevelEntity> Levels { get; set; } = [];
}
