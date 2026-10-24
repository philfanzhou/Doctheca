using Doctheca.Domain.Models.Structure;

namespace Doctheca.Domain.Repositories;

public interface IDocumentStructureAudioRepository
{
    Task<AudioScope?> GetScopeAsync(AudioOwner owner, CancellationToken ct = default);
    Task<List<DocumentStructureAudioModel>> GetByOwnerAsync(AudioOwner owner, CancellationToken ct = default);
    /// <summary>One document-first, definition-share transaction validates the entire retained set before mutation.</summary>
    Task<AudioLinkUpdateResult> CommitAsync(AudioOwner owner, IReadOnlyList<DocumentStructureAudioModel> additions,
        IReadOnlyList<AudioLinkChange> changes, IReadOnlyCollection<Guid> removeIds, DateTimeOffset now,
        bool deleteOnly = false, CancellationToken ct = default);
    /// <summary>Leases immutable keys in ordinal order across uploads and reference-safe cleanup.</summary>
    Task<IAsyncDisposable> AcquireObjectKeyLeaseAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default);

    /// <summary>All audio of a parse ordered by source path.</summary>
    Task<List<DocumentStructureAudioModel>> GetByParseIdAsync(Guid parseId);

    /// <summary>The audio directly linked to one node, ordered by source path.</summary>
    Task<List<DocumentStructureAudioModel>> GetByNodeIdAsync(Guid parseId, Guid nodeId);

    Task<DocumentStructureAudioModel?> GetByIdAsync(Guid parseId, Guid audioId);

    /// <summary>The source paths of this list that the parse already holds.</summary>
    Task<HashSet<string>> GetExistingSourcePathsAsync(Guid parseId, IReadOnlyCollection<string> sourcePaths);

    /// <summary>
    /// Compatibility insert under the shared document/definition commit boundary. Required policy applies
    /// to every retained file; a rejected or missing owner returns false.
    /// </summary>
    Task<bool> InsertAsync(Guid parseId, IReadOnlyList<DocumentStructureAudioModel> items, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies link changes and removals in one document-first, definition-share transaction. Every audio
    /// id must belong to the parse and every node id to its tree, otherwise nothing changes.
    /// </summary>
    Task<AudioLinkUpdateResult> UpdateLinksAsync(Guid parseId, IReadOnlyList<AudioLinkChange> changes,
        IReadOnlyCollection<Guid> removeIds, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>The object keys of every audio file of a parse.</summary>
    Task<List<string>> GetObjectKeysByParseIdAsync(Guid parseId);

    /// <summary>The object keys of every audio file of every parse of a document file.</summary>
    Task<List<string>> GetObjectKeysByDocumentFileIdAsync(Guid documentFileId);

    /// <summary>The keys of this list that some audio row references.</summary>
    Task<HashSet<string>> GetReferencedObjectKeysAsync(IReadOnlyCollection<string> objectKeys);
}
