using Doctheca.Domain.Models.Structure;

namespace Doctheca.Domain.Repositories;

public interface IDocumentStructureRepository
{
    /// <summary>Only current-tree nodes explicitly bound to this parse; history is not an index source.</summary>
    Task<List<DocumentStructureNode>> GetCurrentBindingsAsync(Guid parseId);

    /// <summary>All nodes of a parse ordered by start sequence (parents before children).</summary>
    Task<List<DocumentStructureNode>> GetByParseIdAsync(Guid parseId);

    /// <summary>
    /// Replaces the parse's whole node tree in one transaction that first locks the parse row, so
    /// concurrent saves of the same parse serialize and the last complete save wins. Nodes must be
    /// ordered so every parent precedes its children. Audio linked to a node that the new tree no
    /// longer contains is unlinked in the same transaction. Returns false when the parse does not exist.
    /// </summary>
    Task<bool> ReplaceAsync(Guid parseId, IReadOnlyList<DocumentStructureNode> nodes);

    /// <summary>The ids of nodes in this list that belong to a parse other than <paramref name="parseId"/>.</summary>
    Task<HashSet<Guid>> GetForeignNodeIdsAsync(Guid parseId, IReadOnlyCollection<Guid> nodeIds);
}
