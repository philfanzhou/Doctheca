using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Domain.Services;

/// <summary>
/// Service for QuestionBank pull-mode integration.
/// Provides read-only access to parsed document data for QuestionBank.
/// </summary>
public interface IQuestionBankImportService
{
    /// <summary>
    /// List parses with status=parsed (ready for QuestionBank import).
    /// </summary>
    /// <param name="page">Page number (1-based, auto-corrected to 1 if ≤ 0).</param>
    /// <param name="pageSize">Page size (auto-corrected to 20 if ≤ 0, capped at 100).</param>
    /// <param name="search">Optional file name fuzzy search.</param>
    Task<(List<ImportableParseItem> Items, int TotalCount)> GetImportableListAsync(
        int page,
        int pageSize,
        string? search = null);

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

}
