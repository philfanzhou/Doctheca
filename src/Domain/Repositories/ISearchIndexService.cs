using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Domain.Repositories;

public interface ISearchIndexService
{
    /// <summary>
    /// Ensure index/collection is created
    /// </summary>
    Task EnsureIndexAsync();

    /// <summary>
    /// Index all segments of a document
    /// </summary>
    Task IndexDocumentSegmentsAsync(Guid documentId, string documentTitle, string subject, string grade, string year);

    /// <summary>
    /// Delete all index data for a document
    /// </summary>
    Task DeleteDocumentIndexAsync(Guid documentId);

    /// <summary>
    /// Update document metadata (subject/grade/year)
    /// </summary>
    Task UpdateDocumentMetadataAsync(Guid documentId, string subject, string grade, string year);

    /// <summary>
    /// Exact search (OpenSearch BM25)
    /// </summary>
    Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken);

}
