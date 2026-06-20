using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Domain.Services;

public interface IDocumentDomainService
{
    Task<DocumentModel> CreateDocumentAsync(DocumentModel document);
    Task<DocumentModel?> GetDocumentAsync(Guid id);
    Task<DocumentModel?> GetDocumentByTitleAsync(string title);
    Task<(List<DocumentModel> Items, int TotalCount)> GetDocumentListAsync(
        int page, int size, string? status = null, string? subject = null, string? grade = null, string? keyword = null, string? year = null);
    Task<DocumentModel> UpdateMetadataAsync(
        string title, string? subject, string? grade, string? year, string? tags);
    Task<bool> DeleteDocumentAsync(string title);
    Task<DocumentIngestionJobModel?> GetIngestionJobAsync(Guid documentId);
    Task<List<DocumentIngestionJobModel>> GetPendingJobsAsync();
    Task StartIngestionJobAsync(Guid jobId, string parserVersion, string? ocrVersion);
    Task CompleteIngestionJobAsync(Guid jobId);
    Task FailIngestionJobAsync(Guid jobId, string errorMessage);
    Task CancelIngestionJobAsync(Guid documentId);
    Task<List<DocumentIngestionJobModel>> GetJobsByDocumentIdAsync(Guid documentId);
    Task RetryIngestionAsync(Guid documentId);

    // Segment Refinement
    Task<DocumentSegmentsDto> GetSegmentsAsync(Guid documentId, CancellationToken ct = default);
    Task<RefinementResult> RefineSegmentsAsync(Guid documentId, List<SegmentCorrection> corrections, CancellationToken ct = default);
    Task UpdateDocumentProfileAsync(Guid documentId, string profileJson, string? subject = null, string? grade = null, string? year = null);
}
