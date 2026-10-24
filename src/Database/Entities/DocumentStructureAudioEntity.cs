using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Doctheca.Database.Entities;

/// <summary>
/// A document/tree-owned imported audio file, independently linked to a chapter.
/// The immutable parse_id records storage provenance; parse deletion retains its row and bytes.
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

    [Column("document_file_id")] public Guid DocumentFileId { get; set; }
    [Column("tree_id")] public Guid TreeId { get; set; }

    [Column("parse_id")]
    public Guid? ParseId { get; set; }

    [Column("node_id")]
    public Guid? NodeId { get; set; }

    /// <summary>The file's relative path in the import (folders and name), unique per tree.</summary>
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

    // parse_id is immutable object-key provenance, deliberately not a live parse FK.
}
