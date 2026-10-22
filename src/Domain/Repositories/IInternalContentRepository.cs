using Doctheca.Domain.Models;

namespace Doctheca.Domain.Repositories;

public interface IInternalContentRepository
{
    Task<InternalContentRead> ReadContentAsync(Guid nodeId, InternalContentQuery query, CancellationToken cancellationToken);
    Task<InternalImageRead> ReadImageAsync(Guid nodeId, Guid imageId, string revision, CancellationToken cancellationToken);
}
