using Doctheca.Domain.Models;

namespace Doctheca.Domain.Repositories;

public interface IDocumentParsePageRepository
{
    /// <summary>Read only the explicit parse/page pairs in one database statement.</summary>
    Task<IReadOnlyDictionary<DocumentParsePageKey, DocumentParsePage>> ReadBatchAsync(
        IReadOnlyCollection<DocumentParsePageKey> keys, CancellationToken ct = default);
}
