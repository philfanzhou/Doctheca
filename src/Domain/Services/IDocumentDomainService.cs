using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Domain.Services;

public interface IDocumentDomainService
{
    Task<DocumentModel> CreateDocumentAsync(DocumentModel document);
    Task<DocumentModel?> GetDocumentAsync(Guid id);
    Task<DocumentModel?> GetDocumentByTitleAsync(string title);
    Task<(List<DocumentModel> Items, int TotalCount)> GetDocumentListAsync(
        int page, int size, string? status = null, string? subject = null, string? grade = null, string? keyword = null, string? year = null);
    Task<DocumentIngestionJobModel?> GetIngestionJobAsync(Guid documentId);
    Task<List<DocumentIngestionJobModel>> GetPendingJobsAsync();
    Task StartIngestionJobAsync(Guid jobId, string parserVersion, string? ocrVersion);
    Task UpdateJobProgressAsync(Guid jobId, int progress, string stage);
    Task CompleteIngestionJobAsync(Guid jobId);
    Task FailIngestionJobAsync(Guid jobId, string errorMessage);
    Task<List<DocumentIngestionJobModel>> GetJobsByDocumentIdAsync(Guid documentId);

    // Segment Refinement
    Task<DocumentSegmentsDto> GetSegmentsAsync(Guid documentId, CancellationToken ct = default);
    Task<RefinementResult> RefineSegmentsAsync(Guid documentId, List<SegmentCorrection> corrections, CancellationToken ct = default);
    Task UpdateDocumentProfileAsync(Guid documentId, string profileJson, string? subject = null, string? grade = null, string? year = null);
}
