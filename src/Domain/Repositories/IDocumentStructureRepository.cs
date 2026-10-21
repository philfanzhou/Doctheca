using Doctheca.Domain.Models.Structure;

namespace Doctheca.Domain.Repositories;

public interface IDocumentStructureRepository
{
    /// <summary>All nodes of a parse ordered by start sequence (parents before children).</summary>
    Task<List<DocumentStructureNodeModel>> GetByParseIdAsync(Guid parseId);

    /// <summary>
    /// Replaces the parse's whole node tree in one transaction that first locks the parse row, so
    /// concurrent saves of the same parse serialize and the last complete save wins. Nodes must be
    /// ordered so every parent precedes its children. Returns false when the parse does not exist.
    /// </summary>
    Task<bool> ReplaceAsync(Guid parseId, IReadOnlyList<DocumentStructureNodeModel> nodes);

    /// <summary>The ids of nodes in this list that belong to a parse other than <paramref name="parseId"/>.</summary>
    Task<HashSet<Guid>> GetForeignNodeIdsAsync(Guid parseId, IReadOnlyCollection<Guid> nodeIds);

    /// <summary>Whether any parse of the document file has marked nodes.</summary>
    Task<bool> DocumentFileHasNodesAsync(Guid documentFileId);
}
