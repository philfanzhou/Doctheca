namespace Ruoyu.Study.DocLibrary.Domain.Models;

/// <summary>
/// QuestionBank import status record for a document parse.
/// </summary>
public class DocumentParseImportModel
{
    public Guid Id { get; set; }
    public Guid ParseId { get; set; }
    public Guid ImportedBy { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Note { get; set; }
    public string? ImportedQuestionIds { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// Import status constants for QuestionBank integration.
/// </summary>
public static class ParseImportStatus
{
    public const string Imported = "imported";
    public const string Failed = "failed";
}
