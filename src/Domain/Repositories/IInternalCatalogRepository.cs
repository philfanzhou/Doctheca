using Doctheca.Domain.Models;

namespace Doctheca.Domain.Repositories;

public interface IInternalCatalogRepository
{
    // Returns at most PageSize + 1 rows to let the boundary produce a continuation token.
    Task<IReadOnlyList<InternalDefinition>> ReadDefinitionsAsync(InternalCatalogQuery query, CancellationToken cancellationToken);
    Task<InternalDocumentPage> ReadDocumentsAsync(InternalCatalogQuery query, CancellationToken cancellationToken);
}
