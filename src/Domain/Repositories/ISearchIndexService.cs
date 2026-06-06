using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Domain.Repositories;

public interface ISearchIndexService
{
    /// <summary>
    /// 确保索引/集合已创建
    /// </summary>
    Task EnsureIndexAsync();

    /// <summary>
    /// 索引一个文档的所有segments
    /// </summary>
    Task IndexDocumentSegmentsAsync(Guid documentId, string documentTitle, string subject, string grade, string year);

    /// <summary>
    /// 删除一个文档的所有索引数据
    /// </summary>
    Task DeleteDocumentIndexAsync(Guid documentId);

    /// <summary>
    /// 更新一个文档的元数据（subject/grade/year）
    /// </summary>
    Task UpdateDocumentMetadataAsync(Guid documentId, string subject, string grade, string year);

    /// <summary>
    /// 精确搜索（OpenSearch BM25）
    /// </summary>
    Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken);

    /// <summary>
    /// 混合搜索（精确 + 语义）
    /// </summary>
    Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> HybridSearchAsync(
        string query, bool phrase, int exactTopK, int semanticTopK,
        SearchFilterModel? filter, int pageSize, string? pageToken);
}
