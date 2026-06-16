using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocRetrieval.Domain.Exceptions;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;

namespace Ruoyu.Study.DocRetrieval.Domain.Services;

public class DocumentDomainService : IDocumentDomainService
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentPageRepository _pageRepository;
    private readonly IDocumentSegmentRepository _segmentRepository;
    private readonly IQuestionSegmentRepository _questionRepository;
    private readonly IDocumentOccurrenceRepository _occurrenceRepository;
    private readonly IDocumentIngestionJobRepository _jobRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISearchIndexService? _searchIndexService;
    private readonly ILogger<DocumentDomainService> _logger;

    public DocumentDomainService(
        IDocumentRepository documentRepository,
        IDocumentPageRepository pageRepository,
        IDocumentSegmentRepository segmentRepository,
        IQuestionSegmentRepository questionRepository,
        IDocumentOccurrenceRepository occurrenceRepository,
        IDocumentIngestionJobRepository jobRepository,
        IUnitOfWork unitOfWork,
        ILogger<DocumentDomainService> logger,
        ISearchIndexService? searchIndexService = null)
    {
        _documentRepository = documentRepository;
        _pageRepository = pageRepository;
        _segmentRepository = segmentRepository;
        _questionRepository = questionRepository;
        _occurrenceRepository = occurrenceRepository;
        _jobRepository = jobRepository;
        _unitOfWork = unitOfWork;
        _searchIndexService = searchIndexService;
        _logger = logger;
    }

    public async Task<DocumentModel> CreateDocumentAsync(DocumentModel document)
    {
        ValidateDocumentMetadata(document);

        var existingByTitle = await _documentRepository.GetByTitleAsync(document.Title);
        if (existingByTitle != null)
            throw new DocRetrievalValidationException("Document title already exists");

        var existingByHash = await _documentRepository.GetByFileHashAndStatusAsync(document.FileHash, DocumentStatus.Ready);
        if (existingByHash != null)
            throw new DocRetrievalValidationException("File already imported");

        document.Id = Guid.NewGuid();
        document.Status = DocumentStatus.Pending;
        document.CreatedAt = DateTimeOffset.UtcNow;

        await _documentRepository.AddAsync(document);

        var job = new DocumentIngestionJobModel
        {
            Id = Guid.NewGuid(),
            DocumentId = document.Id,
            Status = DocumentStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _jobRepository.AddAsync(job);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("Document created: {Title}, ingestion job queued", document.Title);
        return document;
    }

    public async Task<DocumentModel?> GetDocumentAsync(Guid id)
    {
        return await _documentRepository.GetByIdAsync(id);
    }

    public async Task<DocumentModel?> GetDocumentByTitleAsync(string title)
    {
        return await _documentRepository.GetByTitleAsync(title);
    }

    public async Task<(List<DocumentModel> Items, int TotalCount)> GetDocumentListAsync(
        int page, int size, string? status = null, string? subject = null, string? grade = null, string? keyword = null, string? year = null)
    {
        if (page <= 0) page = 1;
        if (size <= 0) size = 20;
        if (size > 100) size = 100;

        return await _documentRepository.GetListAsync(page, size, status, subject, grade, keyword, year);
    }

    public async Task<DocumentModel> UpdateMetadataAsync(
        string title, string? subject, string? grade, string? year, string? tags)
    {
        var document = await _documentRepository.GetByTitleAsync(title)
            ?? throw new DocRetrievalValidationException("Document not found");

        if (document.Status != DocumentStatus.Ready)
            throw new DocRetrievalValidationException("Document not ready, metadata update not allowed");

        if (subject != null && !DocRetrievalConstants.IsValidSubject(subject))
            throw new DocRetrievalValidationException("Subject only supports: 英语");

        if (grade != null && !DocRetrievalConstants.IsValidGrade(grade))
            throw new DocRetrievalValidationException($"Invalid grade value, valid values: {string.Join(", ", DocRetrievalConstants.ValidGrades)}");

        if (subject != null) document.Subject = subject;
        if (grade != null) document.Grade = grade;
        if (year != null) document.Year = year;
        if (tags != null) document.Tags = tags;
        document.UpdatedAt = DateTimeOffset.UtcNow;

        await _documentRepository.UpdateAsync(document);
        await _unitOfWork.SaveChangesAsync();

        // Sync update search index metadata
        if (_searchIndexService != null)
        {
            try
            {
                await _searchIndexService.UpdateDocumentMetadataAsync(document.Id, document.Subject, document.Grade, document.Year);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update document search index metadata: {Title}", title);
            }
        }

        _logger.LogInformation("Document metadata updated: {Title}", title);
        return document;
    }

    public async Task<bool> DeleteDocumentAsync(string title)
    {
        var document = await _documentRepository.GetByTitleAsync(title);
        if (document == null) return true; // Idempotent

        // Cancel ongoing ingestion job if document is being ingested
        await CancelIngestionJobAsync(document.Id);

        await _occurrenceRepository.DeleteByDocumentIdAsync(document.Id);
        await _questionRepository.DeleteByDocumentIdAsync(document.Id);
        await _segmentRepository.DeleteByDocumentIdAsync(document.Id);
        await _pageRepository.DeleteByDocumentIdAsync(document.Id);
        await _documentRepository.DeleteAsync(document.Id);
        await _unitOfWork.SaveChangesAsync();

        // Clean up search index
        if (_searchIndexService != null)
        {
            try
            {
                await _searchIndexService.DeleteDocumentIndexAsync(document.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete document search index: {Title}", title);
            }
        }

        _logger.LogInformation("Document deleted: {Title}", title);
        return true;
    }

    public async Task<DocumentIngestionJobModel?> GetIngestionJobAsync(Guid documentId)
    {
        return await _jobRepository.GetByDocumentIdAsync(documentId);
    }

    public async Task<List<DocumentIngestionJobModel>> GetPendingJobsAsync()
    {
        return await _jobRepository.GetByStatusAsync(DocumentStatus.Pending);
    }

    public async Task StartIngestionJobAsync(Guid jobId, string parserVersion, string? ocrVersion)
    {
        var job = await _jobRepository.GetByIdAsync(jobId);
        if (job == null) return;

        job.Status = DocumentStatus.Processing;
        job.StartedAt = DateTimeOffset.UtcNow;
        job.ParserVersion = parserVersion;
        job.OcrVersion = ocrVersion;

        var document = await _documentRepository.GetByIdAsync(job.DocumentId);
        if (document != null)
        {
            document.Status = DocumentStatus.Processing;
            document.UpdatedAt = DateTimeOffset.UtcNow;
            await _documentRepository.UpdateAsync(document);
        }

        await _jobRepository.UpdateAsync(job);
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task CompleteIngestionJobAsync(Guid jobId)
    {
        var job = await _jobRepository.GetByIdAsync(jobId);
        if (job == null) return;

        job.Status = DocumentStatus.Success;
        job.FinishedAt = DateTimeOffset.UtcNow;

        var document = await _documentRepository.GetByIdAsync(job.DocumentId);
        if (document != null)
        {
            document.Status = DocumentStatus.Ready;
            document.UpdatedAt = DateTimeOffset.UtcNow;
            await _documentRepository.UpdateAsync(document);
        }

        await _jobRepository.UpdateAsync(job);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("Document ingestion completed: {DocumentId}", job.DocumentId);
    }

    public async Task FailIngestionJobAsync(Guid jobId, string errorMessage)
    {
        var job = await _jobRepository.GetByIdAsync(jobId);
        if (job == null) return;

        job.Status = DocumentStatus.Failed;
        job.ErrorMessage = errorMessage;
        job.FinishedAt = DateTimeOffset.UtcNow;

        var document = await _documentRepository.GetByIdAsync(job.DocumentId);
        if (document != null)
        {
            document.Status = DocumentStatus.Failed;
            document.UpdatedAt = DateTimeOffset.UtcNow;
            await _documentRepository.UpdateAsync(document);
        }

        await _jobRepository.UpdateAsync(job);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogError("Document ingestion failed: {DocumentId}, reason: {Error}", job.DocumentId, errorMessage);
    }

    /// <summary>
    /// Cancel ingestion job (called when deleting a document being ingested)
    /// </summary>
    public async Task CancelIngestionJobAsync(Guid documentId)
    {
        var job = await _jobRepository.GetByDocumentIdAsync(documentId);
        if (job == null) return;

        // Only pending or processing jobs can be cancelled
        if (job.Status != DocumentStatus.Pending && job.Status != DocumentStatus.Processing)
            return;

        job.Status = DocumentStatus.Cancelled;
        job.FinishedAt = DateTimeOffset.UtcNow;

        var document = await _documentRepository.GetByIdAsync(documentId);
        if (document != null)
        {
            document.Status = DocumentStatus.Cancelled;
            document.UpdatedAt = DateTimeOffset.UtcNow;
            await _documentRepository.UpdateAsync(document);
        }

        await _jobRepository.UpdateAsync(job);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("Ingestion job cancelled: {DocumentId}", documentId);
    }

    /// <summary>
    /// Get all ingestion jobs for a document
    /// </summary>
    public async Task<List<DocumentIngestionJobModel>> GetJobsByDocumentIdAsync(Guid documentId)
    {
        var job = await _jobRepository.GetByDocumentIdAsync(documentId);
        return job != null ? [job] : [];
    }

    private static void ValidateDocumentMetadata(DocumentModel document)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(document.Title))
            errors.Add("Document title cannot be empty");

        if (document.Title?.Length > 200)
            errors.Add("Document title exceeds 200 characters");

        if (string.IsNullOrWhiteSpace(document.Subject))
            errors.Add("Subject cannot be empty");
        else if (!DocRetrievalConstants.IsValidSubject(document.Subject))
            errors.Add("Subject only supports: 英语");

        if (string.IsNullOrWhiteSpace(document.Grade))
            errors.Add("Grade cannot be empty");
        else if (!DocRetrievalConstants.IsValidGrade(document.Grade))
            errors.Add($"Invalid grade value, valid values: {string.Join(", ", DocRetrievalConstants.ValidGrades)}");

        if (string.IsNullOrWhiteSpace(document.Year))
            errors.Add("Year cannot be empty");

        if (string.IsNullOrWhiteSpace(document.FileHash))
            errors.Add("File hash cannot be empty");

        if (errors.Count > 0)
            throw new DocRetrievalValidationException(string.Join("; ", errors));
    }
}
