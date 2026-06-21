using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Oss;
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
    private readonly IOssService? _ossService;
    private readonly ILlmSegmentationService? _llmSegmentation;
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
        ISearchIndexService? searchIndexService = null,
        IOssService? ossService = null,
        ILlmSegmentationService? llmSegmentation = null)
    {
        _documentRepository = documentRepository;
        _pageRepository = pageRepository;
        _segmentRepository = segmentRepository;
        _questionRepository = questionRepository;
        _occurrenceRepository = occurrenceRepository;
        _jobRepository = jobRepository;
        _unitOfWork = unitOfWork;
        _searchIndexService = searchIndexService;
        _ossService = ossService;
        _llmSegmentation = llmSegmentation;
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
            throw new DocRetrievalValidationException("Subject only supports: English");

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

        await _occurrenceRepository.DeleteByDocumentIdAsync(document.Id);
        await _questionRepository.DeleteByDocumentIdAsync(document.Id);
        await _segmentRepository.DeleteByDocumentIdAsync(document.Id);
        await _pageRepository.DeleteByDocumentIdAsync(document.Id);
        await _jobRepository.DeleteByDocumentIdAsync(document.Id);
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

    /// <summary>
    /// 更新任务进度（best-effort：失败不影响任务本身）
    /// </summary>
    public async Task UpdateJobProgressAsync(Guid jobId, int progress, string stage)
    {
        try
        {
            var job = await _jobRepository.GetByIdAsync(jobId);
            if (job == null) return;

            job.Progress = Math.Clamp(progress, 0, 100);
            job.ProgressStage = stage;

            await _jobRepository.UpdateAsync(job);
            await _unitOfWork.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // 进度更新失败不能影响任务
            _logger.LogDebug(ex, "更新任务进度失败：{JobId}", jobId);
        }
    }

    public async Task FailIngestionJobAsync(Guid jobId, string errorMessage)
    {
        try
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
        }
        catch (Exception ex)
        {
            // DbContext may be in a corrupted state from a prior SaveChanges failure.
            // Clear the tracker and re-fetch to persist the failure status.
            _logger.LogWarning(ex, "First attempt to mark job as failed failed, clearing change tracker and retrying");
            _unitOfWork.ClearChangeTracker();

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
        }

        _logger.LogError("Document ingestion failed: {DocumentId}, reason: {Error}", jobId, errorMessage);
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
    /// Retry failed document: clear old data, reset status, create new job.
    /// </summary>
    public async Task RetryIngestionAsync(Guid documentId)
    {
        var document = await _documentRepository.GetByIdAsync(documentId)
            ?? throw new InvalidOperationException($"Document not found: {documentId}");

        if (document.Status != DocumentStatus.Failed)
            throw new DocRetrievalValidationException("Document status is not failed, cannot retry");

        // Clear old parsed data
        await _occurrenceRepository.DeleteByDocumentIdAsync(documentId);
        await _questionRepository.DeleteByDocumentIdAsync(documentId);
        await _segmentRepository.DeleteByDocumentIdAsync(documentId);
        await _pageRepository.DeleteByDocumentIdAsync(documentId);

        // Reset document status
        document.Status = DocumentStatus.Pending;
        document.UpdatedAt = DateTimeOffset.UtcNow;
        await _documentRepository.UpdateAsync(document);

        // Create new ingestion job
        var job = new DocumentIngestionJobModel
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            Status = DocumentStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _jobRepository.AddAsync(job);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("Document ingestion retry queued: {DocumentId}, new job: {JobId}", documentId, job.Id);
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

        // Subject and grade are optional (AI auto-fills if empty)
        if (!string.IsNullOrWhiteSpace(document.Subject) && !DocRetrievalConstants.IsValidSubject(document.Subject))
            errors.Add("Subject only supports: English");

        if (!string.IsNullOrWhiteSpace(document.Grade) && !DocRetrievalConstants.IsValidGrade(document.Grade))
            errors.Add($"Invalid grade value, valid values: {string.Join(", ", DocRetrievalConstants.ValidGrades)}");

        if (string.IsNullOrWhiteSpace(document.FileHash))
            errors.Add("File hash cannot be empty");

        if (errors.Count > 0)
            throw new DocRetrievalValidationException(string.Join("; ", errors));
    }

    public async Task UpdateDocumentProfileAsync(Guid documentId, string profileJson, string? subject = null, string? grade = null, string? year = null)
    {
        var document = await _documentRepository.GetByIdAsync(documentId);
        if (document == null) return;

        document.LlmProfileJson = profileJson;
        if (!string.IsNullOrWhiteSpace(subject)) document.Subject = subject;
        if (!string.IsNullOrWhiteSpace(grade)) document.Grade = grade;
        if (!string.IsNullOrWhiteSpace(year)) document.Year = year;
        document.UpdatedAt = DateTimeOffset.UtcNow;
        await _documentRepository.UpdateAsync(document);
        await _unitOfWork.SaveChangesAsync();
    }

    #region Segment Refinement

    public async Task<DocumentSegmentsDto> GetSegmentsAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await _documentRepository.GetByIdAsync(documentId)
            ?? throw new KeyNotFoundException($"Document not found: {documentId}");

        var segments = await _segmentRepository.GetByDocumentIdAsync(documentId);
        var pages = await _pageRepository.GetByDocumentIdAsync(documentId);

        var pageLookup = pages.ToDictionary(p => p.Id, p => p.PageNumber);

        DocumentProfile? profile = null;
        if (!string.IsNullOrEmpty(document.LlmProfileJson))
        {
            try
            {
                profile = JsonSerializer.Deserialize<DocumentProfile>(document.LlmProfileJson);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize LLM profile for document {DocumentId}", documentId);
            }
        }

        var segmentDtos = segments
            .OrderBy(s => s.SentenceId)
            .Select(s => new SegmentDto
            {
                Id = s.Id,
                SentenceId = s.SentenceId,
                SegmentType = s.SegmentType,
                Text = s.Text,
                StartOffset = s.StartOffset,
                EndOffset = s.EndOffset,
                PageNumber = pageLookup.TryGetValue(s.PageId, out var pn) ? pn : 0
            })
            .ToList();

        return new DocumentSegmentsDto
        {
            DocumentId = documentId,
            Title = document.Title,
            Status = document.Status,
            Profile = profile,
            Segments = segmentDtos,
            TotalCount = segmentDtos.Count
        };
    }

    public async Task<RefinementResult> RefineSegmentsAsync(
        Guid documentId,
        List<SegmentCorrection> corrections,
        CancellationToken ct = default)
    {
        const int MaxCorrections = 20;
        if (corrections.Count > MaxCorrections)
            throw new DocRetrievalValidationException($"Too many corrections: {corrections.Count}, max is {MaxCorrections}");

        if (corrections.Count == 0)
            throw new DocRetrievalValidationException("Corrections list cannot be empty");

        var document = await _documentRepository.GetByIdAsync(documentId)
            ?? throw new KeyNotFoundException($"Document not found: {documentId}");

        if (document.Status != DocumentStatus.Ready)
            throw new DocRetrievalValidationException("Document not ready, refinement not allowed");

        if (_ossService == null || _llmSegmentation == null)
            throw new DocRetrievalValidationException("LLM segmentation service not configured");

        // 1. Get current segments for backup
        var currentSegments = await _segmentRepository.GetByDocumentIdAsync(documentId);
        var pages = await _pageRepository.GetByDocumentIdAsync(documentId);
        var pageLookup = pages.ToDictionary(p => p.Id, p => p.PageNumber);

        var backupData = new SegmentBackupData
        {
            Segments = currentSegments.Select(s => new SegmentDto
            {
                Id = s.Id,
                SentenceId = s.SentenceId,
                SegmentType = s.SegmentType,
                Text = s.Text,
                StartOffset = s.StartOffset,
                EndOffset = s.EndOffset,
                PageNumber = pageLookup.TryGetValue(s.PageId, out var pn) ? pn : 0
            }).ToList(),
            ProfileJson = document.LlmProfileJson
        };

        var backupId = Guid.NewGuid();

        // 2. Download file and extract text for LLM
        using var fileStream = await _ossService.DownloadAsync(document.FilePath);
        if (fileStream == null)
            throw new InvalidOperationException($"File not found in OSS: {document.FilePath}");

        // Extract text preview (first 2000 chars)
        var textPreview = await ExtractTextPreviewAsync(fileStream, document.SourceType, ct);

        // 3. Parse original profile
        DocumentProfile? originalProfile = null;
        if (!string.IsNullOrEmpty(document.LlmProfileJson))
        {
            try
            {
                originalProfile = JsonSerializer.Deserialize<DocumentProfile>(document.LlmProfileJson);
            }
            catch (JsonException) { /* ignore */ }
        }
        originalProfile ??= new DocumentProfile();

        // 4. Call LLM refinement
        var newProfile = await _llmSegmentation.RefineProfileAsync(textPreview, originalProfile, corrections, ct);
        _logger.LogInformation("LLM profile refinement completed: Subject={Subject}, DocType={DocType}, Strategy={Strategy}",
            newProfile.Subject, newProfile.DocType, newProfile.SegmentStrategy);

        // 5. Re-segment each page's text blocks
        // We need to re-parse the document to get text blocks, then re-segment with corrections
        // For simplicity, we'll re-use the existing segments' text and re-segment them
        // This is a pragmatic approach: take all segment texts as input, re-segment with LLM

        // Group segments by page and reconstruct page text
        var segmentsByPage = currentSegments
            .GroupBy(s => s.PageId)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.StartOffset).ToList());

        var allNewSegments = new List<DocumentSegmentModel>();
        var allNewOccurrences = new List<DocumentOccurrenceModel>();

        foreach (var (pageId, pageSegments) in segmentsByPage)
        {
            var pageText = string.Join(" ", pageSegments.Select(s => s.Text));
            var pageNumber = pageLookup.TryGetValue(pageId, out var pn) ? pn : 0;

            try
            {
                var newSegments = await _llmSegmentation.RefineSegmentTextAsync(pageText, newProfile, corrections, ct);

                for (var i = 0; i < newSegments.Count; i++)
                {
                    var seg = newSegments[i];
                    var segmentId = Guid.NewGuid();
                    var sentenceId = $"p{pageNumber}-s{i + 1}";

                    allNewSegments.Add(new DocumentSegmentModel
                    {
                        Id = segmentId,
                        DocumentId = documentId,
                        PageId = pageId,
                        SentenceId = sentenceId,
                        SegmentType = seg.SegmentType,
                        Text = seg.Text,
                        StartOffset = seg.StartOffset,
                        EndOffset = seg.EndOffset,
                        CreatedAt = DateTimeOffset.UtcNow
                    });

                    // Generate tokens for occurrences
                    var tokens = TokenizeForRefinement(seg.Text);
                    foreach (var token in tokens)
                    {
                        allNewOccurrences.Add(new DocumentOccurrenceModel
                        {
                            Id = Guid.NewGuid(),
                            DocumentId = documentId,
                            SegmentId = segmentId,
                            TokenText = token.TokenText,
                            TokenStem = token.TokenStem,
                            StartOffset = token.StartOffset,
                            EndOffset = token.EndOffset,
                            CreatedAt = DateTimeOffset.UtcNow
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LLM refinement failed for page {PageNumber}, keeping original segments", pageNumber);
                // Create NEW instances (not tracked) to avoid EF Core conflict with deleted entities
                foreach (var seg in pageSegments)
                {
                    var newSeg = new DocumentSegmentModel
                    {
                        Id = Guid.NewGuid(),
                        DocumentId = seg.DocumentId,
                        PageId = seg.PageId,
                        SentenceId = seg.SentenceId,
                        SegmentType = seg.SegmentType,
                        Text = seg.Text,
                        StartOffset = seg.StartOffset,
                        EndOffset = seg.EndOffset,
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    allNewSegments.Add(newSeg);

                    // Re-generate occurrences for the kept segment
                    var tokens = TokenizeForRefinement(seg.Text);
                    foreach (var token in tokens)
                    {
                        allNewOccurrences.Add(new DocumentOccurrenceModel
                        {
                            Id = Guid.NewGuid(),
                            DocumentId = documentId,
                            SegmentId = newSeg.Id,
                            TokenText = token.TokenText,
                            TokenStem = token.TokenStem,
                            StartOffset = token.StartOffset,
                            EndOffset = token.EndOffset,
                            CreatedAt = DateTimeOffset.UtcNow
                        });
                    }
                }
            }
        }

        // 6. Delete old data
        await _occurrenceRepository.DeleteByDocumentIdAsync(documentId);
        await _segmentRepository.DeleteByDocumentIdAsync(documentId);

        // 7. Write new data
        if (allNewSegments.Count > 0)
        {
            await _segmentRepository.AddRangeAsync(allNewSegments);
        }
        if (allNewOccurrences.Count > 0)
        {
            await _occurrenceRepository.AddRangeAsync(allNewOccurrences);
        }

        // 8. Update document profile
        document.LlmProfileJson = JsonSerializer.Serialize(newProfile);
        document.UpdatedAt = DateTimeOffset.UtcNow;
        await _documentRepository.UpdateAsync(document);

        // 9. Save backup
        // Note: We need a repository for backups. For now, save via DbContext directly.
        // This will be handled by the endpoint or a dedicated service.

        await _unitOfWork.SaveChangesAsync();

        // 10. Rebuild search index
        if (_searchIndexService != null)
        {
            try
            {
                await _searchIndexService.DeleteDocumentIndexAsync(documentId);
                await _searchIndexService.IndexDocumentSegmentsAsync(
                    documentId, document.Title, document.Subject, document.Grade, document.Year);
                _logger.LogInformation("Search index rebuilt after refinement: {DocumentId}", documentId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to rebuild search index after refinement: {DocumentId}", documentId);
            }
        }

        _logger.LogInformation("Document refinement completed: {DocumentId}, {SegmentCount} new segments",
            documentId, allNewSegments.Count);

        return new RefinementResult
        {
            DocumentId = documentId,
            BackupId = backupId,
            CorrectionCount = corrections.Count,
            Message = $"Refinement completed, {allNewSegments.Count} new segments created",
            BackupDataJson = JsonSerializer.Serialize(backupData)
        };
    }

    private static async Task<string> ExtractTextPreviewAsync(System.IO.Stream fileStream, string sourceType, CancellationToken ct)
    {
        // Simple text extraction for preview purposes
        // Read first 4096 bytes and try to extract readable text
        var buffer = new byte[4096];
        var totalRead = 0;
        using var ms = new System.IO.MemoryStream();
        while (totalRead < 4096)
        {
            var read = await fileStream.ReadAsync(buffer.AsMemory(totalRead, Math.Min(1024, 4096 - totalRead)), ct);
            if (read == 0) break;
            totalRead += read;
        }
        fileStream.Position = 0; // Reset for potential re-use

        // For PDF, try to extract text; for others, use raw bytes as approximation
        var text = Encoding.UTF8.GetString(buffer, 0, totalRead);
        // Clean up non-printable characters
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (c >= 32 || c == '\n' || c == '\r' || c == '\t')
                sb.Append(c);
        }
        var result = sb.ToString();
        return result.Length > 2000 ? result[..2000] : result;
    }

    private static List<ParsedToken> TokenizeForRefinement(string text)
    {
        var tokens = new List<ParsedToken>();
        if (string.IsNullOrWhiteSpace(text)) return tokens;

        var regex = new System.Text.RegularExpressions.Regex(@"[a-zA-Z]+");
        foreach (System.Text.RegularExpressions.Match match in regex.Matches(text))
        {
            var word = match.Value;
            tokens.Add(new ParsedToken
            {
                TokenText = word,
                TokenStem = word.ToLowerInvariant(), // Simplified stemming for refinement
                StartOffset = match.Index,
                EndOffset = match.Index + match.Length
            });
        }
        return tokens;
    }

    #endregion
}
