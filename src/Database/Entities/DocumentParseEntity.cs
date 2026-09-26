using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Doctheca.Domain.Models;

namespace Doctheca.Database.Entities;

[Table("document_parses")]
public class DocumentParseEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("document_file_id")]
    [Required]
    public Guid DocumentFileId { get; set; }

    [Column("model_version")]
    [Required]
    [MaxLength(20)]
    public string ModelVersion { get; set; } = "vlm";

    [Column("status")]
    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = DocumentParseStatus.Pending;

    [Column("external_task_id")]
    [MaxLength(100)]
    public string? ExternalTaskId { get; set; }

    [Column("structadoc_parse_run_id")]
    public Guid? StructaDocParseRunId { get; set; }

    [Column("markdown_content")]
    public string? MarkdownContent { get; set; }

    [Column("content_list")]
    public string? ContentList { get; set; }

    [Column("content_list_v2")]
    public string? ContentListV2 { get; set; }

    [Column("model_json")]
    public string? ModelJson { get; set; }

    [Column("layout_json")]
    public string? LayoutJson { get; set; }

    [Column("zip_path")]
    [MaxLength(500)]
    public string? ZipPath { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("parsed_at")]
    public DateTimeOffset? ParsedAt { get; set; }

    [ForeignKey(nameof(DocumentFileId))]
    public DocumentFileEntity? DocumentFile { get; set; }
}
