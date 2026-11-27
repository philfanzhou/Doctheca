using Doctheca.Domain.Models.Structure;

namespace Doctheca.Domain.Repositories;

public interface IDocumentStructureAudioRepository
{
    Task<AudioScope?> GetScopeAsync(AudioOwner owner, CancellationToken ct = default);
    Task<List<DocumentStructureAudio>> GetByOwnerAsync(AudioOwner owner, CancellationToken ct = default);
    /// <summary>One document-first, definition-share transaction validates the entire retained set before mutation.</summary>
    Task<AudioLinkUpdateResult> CommitAsync(AudioOwner owner, IReadOnlyList<DocumentStructureAudio> additions,
        IReadOnlyList<AudioLinkChange> changes, IReadOnlyCollection<Guid> removeIds, DateTimeOffset now,
        bool deleteOnly = false, CancellationToken ct = default);
    /// <summary>Leases immutable keys in ordinal order across uploads and reference-safe cleanup.</summary>
    Task<IAsyncDisposable> AcquireObjectKeyLeaseAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default);

    /// <summary>The keys of this list that some audio row references.</summary>
    Task<HashSet<string>> GetReferencedObjectKeysAsync(IReadOnlyCollection<string> objectKeys);
}
