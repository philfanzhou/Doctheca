using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Domain.Services;

/// <summary>
/// Service for QuestionBank pull-mode integration.
/// Provides access to parsed document data (importable list, structured blocks, images)
/// and tracks QuestionBank import status to prevent duplicate processing.
/// </summary>
public interface IQuestionBankImportService
{
    /// <summary>
    /// List parses with status=parsed (ready for QuestionBank import).
    /// </summary>
    /// <param name="page">Page number (1-based, auto-corrected to 1 if ≤ 0).</param>
    /// <param name="pageSize">Page size (auto-corrected to 20 if ≤ 0, capped at 100).</param>
    /// <param name="search">Optional file name fuzzy search.</param>
    /// <param name="includeImported">When false (default), excludes parses already marked as imported.</param>
    Task<(List<ImportableParseItem> Items, int TotalCount)> GetImportableListAsync(
        int page,
        int pageSize,
        string? search = null,
        bool includeImported = false);

    /// <summary>
    /// Get structured blocks for a parse, with optional filters.
    /// </summary>
    /// <param name="parseId">Parse record ID.</param>
    /// <param name="pageId">Optional MinerU page ID filter (0 is a valid value).</param>
    /// <param name="blockType">Optional block type filter (text/image/table/title etc.).</param>
    /// <param name="page">Page number (1-based, auto-corrected to 1 if ≤ 0).</param>
    /// <param name="pageSize">Page size (auto-corrected to 50 if ≤ 0, capped at 200).</param>
    /// <exception cref="KeyNotFoundException">Parse not found.</exception>
    /// <exception cref="InvalidOperationException">Parse status is not 'parsed'.</exception>
    Task<(List<ParseBlockItem> Items, int TotalCount)> GetBlocksAsync(
        Guid parseId,
        int? pageId,
        string? blockType,
        int page,
        int pageSize);

    /// <summary>
    /// Get image metadata and stream by image ID.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Image not found.</exception>
    Task<ParseImageBlob> GetImageBlobAsync(Guid imageId);

    /// <summary>
    /// Upsert QuestionBank import status for a parse.
    /// </summary>
    /// <param name="parseId">Parse record ID.</param>
    /// <param name="importedBy">QuestionBank service account ID.</param>
    /// <param name="status">Import status: 'imported' or 'failed'.</param>
    /// <param name="note">Optional note (failure reason, imported question count, etc.).</param>
    /// <param name="importedQuestionIds">Optional imported QuestionBank question ID list.</param>
    /// <exception cref="KeyNotFoundException">Parse not found.</exception>
    /// <exception cref="ArgumentException">Invalid status value.</exception>
    /// <exception cref="InvalidOperationException">Parse is already marked as imported.</exception>
    Task<ImportStatusResult> UpsertImportStatusAsync(
        Guid parseId,
        Guid importedBy,
        string status,
        string? note,
        string? importedQuestionIds);
}

/// <summary>
/// Importable parse item returned in the list endpoint.
/// </summary>
public record ImportableParseItem(
    Guid ParseId,
    Guid FileId,
    string FileName,
    string ModelVersion,
    DateTimeOffset? ParsedAt,
    string? ImportStatus,
    DateTimeOffset? ImportedAt);

/// <summary>
/// Structured block item with optional image metadata.
/// </summary>
public record ParseBlockItem(
    Guid Id,
    Guid ParseId,
    int PageId,
    int SortIndex,
    string BlockType,
    string? TextContent,
    string? ImageName,
    string? ImagePath,
    string? ImageUrl,
    object? BlockData);

/// <summary>
/// Image binary blob with metadata, ready for streaming response.
/// </summary>
public record ParseImageBlob(
    string ImageName,
    string ContentType,
    Stream Stream);

/// <summary>
/// Import status upsert result.
/// </summary>
public record ImportStatusResult(
    Guid ParseId,
    string ImportStatus,
    DateTimeOffset ImportedAt,
    DateTimeOffset? UpdatedAt);
