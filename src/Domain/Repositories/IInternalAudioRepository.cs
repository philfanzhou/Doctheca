using Doctheca.Domain.Models;

namespace Doctheca.Domain.Repositories;

public interface IInternalAudioRepository
{
    Task<InternalAudioMembership?> ReadAudioAsync(Guid nodeId, Guid? audioId, CancellationToken cancellationToken);
}
