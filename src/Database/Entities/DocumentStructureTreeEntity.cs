using System.ComponentModel.DataAnnotations.Schema;

namespace Doctheca.Database.Entities;

/// <summary>A persistent chapter tree owned by a document; origin is immutable provenance.</summary>
[Table("document_structure_trees")]
public sealed class DocumentStructureTreeEntity
{
    [Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("document_file_id")] public Guid DocumentFileId { get; set; }
    [Column("origin_parse_id")] public Guid? OriginParseId { get; set; }
}
