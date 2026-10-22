using Doctheca.Domain.Models;

namespace Doctheca.Domain.Repositories;

public interface IDocumentParsePageRepository
{
    /// <summary>Atomically replace a parse's complete set, including an empty replacement.</summary>
    Task ReplaceAsync(Guid parseId, IReadOnlyCollection<DocumentParsePageModel> pages, CancellationToken ct = default);

    /// <summary>Read only the explicit parse/page pairs in one database statement.</summary>
    Task<IReadOnlyDictionary<DocumentParsePageKey, DocumentParsePageModel>> ReadBatchAsync(
        IReadOnlyCollection<DocumentParsePageKey> keys, CancellationToken ct = default);
}
