using Doctheca.Domain.Models;

namespace Doctheca.Domain.Repositories;

public interface IInternalStructureRepository
{
    /// <summary>Reads existence, current parse and the entire tree in one repeatable-read snapshot.</summary>
    Task<InternalStructureRead> ReadTreeAsync(Guid documentFileId, CancellationToken cancellationToken);

    /// <summary>Returns null for absent nodes and nodes outside their document's selected current parse.</summary>
    Task<InternalCurrentStructureNode?> ReadCurrentNodeAsync(Guid nodeId, CancellationToken cancellationToken);
}
