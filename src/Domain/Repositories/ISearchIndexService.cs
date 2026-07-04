using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Domain.Repositories;

public interface ISearchIndexService
{
    /// <summary>
    /// Ensure index/collection is created
    /// </summary>
    Task EnsureIndexAsync();

    /// <summary>
    /// Index all segments of a document (legacy LLM segmentation pipeline).
    /// Kept for backward compatibility; new code should use IndexParseBlocksAsync.
    /// </summary>
    Task IndexDocumentSegmentsAsync(Guid documentId, string documentTitle, string subject, string grade, string year);

    /// <summary>
    /// Index all blocks of a parse to OpenSearch. Called after MinerU parse completes.
    /// Idempotent: re-indexing overwrites existing documents (same _id = block_{blockId}).
    /// </summary>
    Task IndexParseBlocksAsync(Guid parseId, Guid documentFileId, string fileName, string? subject, string? grade, string? year);

    /// <summary>
    /// Delete all OpenSearch documents for a document (legacy LLM pipeline).
    /// Kept for backward compatibility.
    /// </summary>
    Task DeleteDocumentIndexAsync(Guid documentId);

    /// <summary>
    /// Delete all OpenSearch documents for a parse. Called when parse result is deleted.
    /// </summary>
    Task DeleteParseIndexAsync(Guid parseId);

    /// <summary>
    /// Delete all OpenSearch documents for a document file (across all parses).
    /// Called when document file is deleted.
    /// </summary>
    Task DeleteDocumentFileIndexAsync(Guid documentFileId);

    /// <summary>
    /// Update document metadata (subject/grade/year) (legacy LLM pipeline).
    /// Kept for backward compatibility.
    /// </summary>
    Task UpdateDocumentMetadataAsync(Guid documentId, string subject, string grade, string year);

    /// <summary>
    /// Update subject/grade/year for all indexed blocks of a document file.
    /// Used when metadata is set/updated after initial indexing (e.g., by LLM analysis or manual edit).
    /// Best-effort: failures are logged but do not throw.
    /// </summary>
    Task UpdateDocumentFileMetadataAsync(Guid documentFileId, string? subject, string? grade, string? year);

    /// <summary>
    /// Exact search (OpenSearch BM25)
    /// </summary>
    Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken);

}
