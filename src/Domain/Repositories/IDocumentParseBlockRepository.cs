using Doctheca.Domain.Models;

namespace Doctheca.Domain.Repositories;

public interface IDocumentParseBlockRepository
{
    /// <summary>
    /// All blocks of a parse, ordered by page then sort_index.
    /// </summary>
    Task<List<DocumentParseBlock>> GetByParseIdAsync(Guid parseId);

    /// <summary>
    /// One page of a parse's blocks in global <c>sequence</c> order: blocks with
    /// sequence &gt; <paramref name="afterSequence"/> (all when null) and, when given, within the
    /// inclusive [<paramref name="fromSequence"/>, <paramref name="toSequence"/>] range. Blocks
    /// without a sequence are not returned.
    /// </summary>
    Task<List<DocumentParseBlock>> GetPageBySequenceAsync(
        Guid parseId, int? afterSequence, int limit, int? fromSequence = null, int? toSequence = null);

    /// <summary>All non-null block sequences of a parse.</summary>
    Task<HashSet<int>> GetSequencesAsync(Guid parseId);
}
