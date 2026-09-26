using Doctheca.Domain.Models;

namespace Doctheca.Domain.Repositories;

public interface IDocumentParseBlockRepository
{
    /// <summary>
    /// Bulk insert blocks. Existing blocks for the same parse_id are deleted first
    /// (overwrite strategy: latest parse wins).
    /// </summary>
    Task AddBlocksAsync(Guid parseId, IEnumerable<DocumentParseBlockModel> blocks);

    /// <summary>
    /// Get all blocks for a parse, ordered by page then sort_index.
    /// </summary>
    Task<List<DocumentParseBlockModel>> GetByParseIdAsync(Guid parseId);

    /// <summary>
    /// Get blocks for a specific page of a parse.
    /// </summary>
    Task<List<DocumentParseBlockModel>> GetByParseAndPageAsync(Guid parseId, int pageId);
}
