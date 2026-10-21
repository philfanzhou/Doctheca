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

    /// <summary>
    /// One page of a parse's blocks in global <c>sequence</c> order: blocks with
    /// sequence &gt; <paramref name="afterSequence"/> (all when null) and, when given, within the
    /// inclusive [<paramref name="fromSequence"/>, <paramref name="toSequence"/>] range. Blocks
    /// without a sequence are not returned.
    /// </summary>
    Task<List<DocumentParseBlockModel>> GetPageBySequenceAsync(
        Guid parseId, int? afterSequence, int limit, int? fromSequence = null, int? toSequence = null);

    /// <summary>All non-null block sequences of a parse.</summary>
    Task<HashSet<int>> GetSequencesAsync(Guid parseId);
}
