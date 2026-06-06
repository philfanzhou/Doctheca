using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Domain.Repositories;

public interface IQdrantService
{
    Task EnsureCollectionAsync();
    Task IndexDocumentVectorsAsync(Guid documentId, string documentTitle, string subject, string grade, string year);
    Task DeleteDocumentVectorsAsync(Guid documentId);
    Task<List<SearchResultModel>> SemanticSearchAsync(string query, int topK, SearchFilterModel? filter);
}
