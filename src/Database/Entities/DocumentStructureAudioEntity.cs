using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Doctheca.Database.Entities;

/// <summary>
/// An imported audio file of one parse, optionally linked to one of the parse's structure nodes
/// (issue #90). The bytes live in object storage under <c>object_key</c>; deleting the parse
/// deletes its rows, and the application deletes the objects.
/// </summary>
/// <remarks>
/// <c>node_id</c> has no foreign key on purpose: a structure save replaces the whole node tree
/// (delete and re-insert with the same ids), which an ON DELETE rule would see as removing every
/// node. The tree save unlinks audio of removed nodes in the same transaction instead.
/// </remarks>
[Table("document_structure_audio")]
public class DocumentStructureAudioEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("parse_id")]
    [Required]
    public Guid ParseId { get; set; }

    [Column("node_id")]
    public Guid? NodeId { get; set; }

    /// <summary>The file's relative path in the import (folders and name), unique per parse.</summary>
    [Column("source_path")]
    [Required]
    [MaxLength(1000)]
    public string SourcePath { get; set; } = string.Empty;

    [Column("content_type")]
    [Required]
    [MaxLength(100)]
    public string ContentType { get; set; } = string.Empty;

    [Column("size_bytes")]
    [Required]
    public long SizeBytes { get; set; }

    /// <summary>Never returned by any endpoint.</summary>
    [Column("object_key")]
    [Required]
    [MaxLength(500)]
    public string ObjectKey { get; set; } = string.Empty;

    [Column("match_status")]
    [Required]
    [MaxLength(32)]
    public string MatchStatus { get; set; } = string.Empty;

    [Column("link_source")]
    [MaxLength(16)]
    public string? LinkSource { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [ForeignKey(nameof(ParseId))]
    public DocumentParseEntity? Parse { get; set; }
}
