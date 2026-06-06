using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;

namespace Ruoyu.Study.DocRetrieval.Domain.Services;

public class DocRetrievalValidationException : Exception
{
    public DocRetrievalValidationException(string message) : base(message) { }
}

public class DocumentDomainService
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
            throw new DocRetrievalValidationException("文档名已存在");

        var existingByHash = await _documentRepository.GetByFileHashAndStatusAsync(document.FileHash, "ready");
        if (existingByHash != null)
            throw new DocRetrievalValidationException("该文件已被导入");

        document.Id = Guid.NewGuid();
        document.Status = "pending";
        document.CreatedAt = DateTimeOffset.UtcNow;

        await _documentRepository.AddAsync(document);

        var job = new DocumentIngestionJobModel
        {
            Id = Guid.NewGuid(),
            DocumentId = document.Id,
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _jobRepository.AddAsync(job);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("文档已创建：{Title}，导入任务已排队", document.Title);
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
            ?? throw new DocRetrievalValidationException("文档不存在");

        if (document.Status != "ready")
            throw new DocRetrievalValidationException("文档未就绪，不允许修改元数据");

        if (subject != null && !DocRetrievalConstants.IsValidSubject(subject))
            throw new DocRetrievalValidationException("学科仅支持：英语");

        if (grade != null && !DocRetrievalConstants.IsValidGrade(grade))
            throw new DocRetrievalValidationException($"年级取值非法，有效值：{string.Join("、", DocRetrievalConstants.ValidGrades)}");

        if (subject != null) document.Subject = subject;
        if (grade != null) document.Grade = grade;
        if (year != null) document.Year = year;
        if (tags != null) document.Tags = tags;
        document.UpdatedAt = DateTimeOffset.UtcNow;

        await _documentRepository.UpdateAsync(document);
        await _unitOfWork.SaveChangesAsync();

        // 同步更新搜索索引中的元数据
        if (_searchIndexService != null)
        {
            try
            {
                await _searchIndexService.UpdateDocumentMetadataAsync(document.Id, document.Subject, document.Grade, document.Year);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新文档搜索索引元数据失败：{Title}", title);
            }
        }

        _logger.LogInformation("文档元数据已更新：{Title}", title);
        return document;
    }

    public async Task<bool> DeleteDocumentAsync(string title)
    {
        var document = await _documentRepository.GetByTitleAsync(title);
        if (document == null) return true; // 幂等

        await _occurrenceRepository.DeleteByDocumentIdAsync(document.Id);
        await _questionRepository.DeleteByDocumentIdAsync(document.Id);
        await _segmentRepository.DeleteByDocumentIdAsync(document.Id);
        await _pageRepository.DeleteByDocumentIdAsync(document.Id);
        await _documentRepository.DeleteAsync(document.Id);
        await _unitOfWork.SaveChangesAsync();

        // 清理搜索索引
        if (_searchIndexService != null)
        {
            try
            {
                await _searchIndexService.DeleteDocumentIndexAsync(document.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除文档搜索索引失败：{Title}", title);
            }
        }

        _logger.LogInformation("文档已删除：{Title}", title);
        return true;
    }

    public async Task<DocumentIngestionJobModel?> GetIngestionJobAsync(Guid documentId)
    {
        return await _jobRepository.GetByDocumentIdAsync(documentId);
    }

    public async Task<List<DocumentIngestionJobModel>> GetPendingJobsAsync()
    {
        return await _jobRepository.GetByStatusAsync("pending");
    }

    public async Task StartIngestionJobAsync(Guid jobId, string parserVersion, string? ocrVersion)
    {
        var job = await _jobRepository.GetByIdAsync(jobId);
        if (job == null) return;

        job.Status = "processing";
        job.StartedAt = DateTimeOffset.UtcNow;
        job.ParserVersion = parserVersion;
        job.OcrVersion = ocrVersion;

        var document = await _documentRepository.GetByIdAsync(job.DocumentId);
        if (document != null)
        {
            document.Status = "processing";
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

        job.Status = "success";
        job.FinishedAt = DateTimeOffset.UtcNow;

        var document = await _documentRepository.GetByIdAsync(job.DocumentId);
        if (document != null)
        {
            document.Status = "ready";
            document.UpdatedAt = DateTimeOffset.UtcNow;
            await _documentRepository.UpdateAsync(document);
        }

        await _jobRepository.UpdateAsync(job);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("文档导入完成：{DocumentId}", job.DocumentId);
    }

    public async Task FailIngestionJobAsync(Guid jobId, string errorMessage)
    {
        var job = await _jobRepository.GetByIdAsync(jobId);
        if (job == null) return;

        job.Status = "failed";
        job.ErrorMessage = errorMessage;
        job.FinishedAt = DateTimeOffset.UtcNow;

        var document = await _documentRepository.GetByIdAsync(job.DocumentId);
        if (document != null)
        {
            document.Status = "failed";
            document.UpdatedAt = DateTimeOffset.UtcNow;
            await _documentRepository.UpdateAsync(document);
        }

        await _jobRepository.UpdateAsync(job);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogError("文档导入失败：{DocumentId}，原因：{Error}", job.DocumentId, errorMessage);
    }

    private static void ValidateDocumentMetadata(DocumentModel document)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(document.Title))
            errors.Add("文档名不能为空");

        if (document.Title?.Length > 200)
            errors.Add("文档名超过200字符");

        if (string.IsNullOrWhiteSpace(document.Subject))
            errors.Add("学科不能为空");
        else if (!DocRetrievalConstants.IsValidSubject(document.Subject))
            errors.Add("学科仅支持：英语");

        if (string.IsNullOrWhiteSpace(document.Grade))
            errors.Add("年级不能为空");
        else if (!DocRetrievalConstants.IsValidGrade(document.Grade))
            errors.Add($"年级取值非法，有效值：{string.Join("、", DocRetrievalConstants.ValidGrades)}");

        if (string.IsNullOrWhiteSpace(document.Year))
            errors.Add("年份不能为空");

        if (string.IsNullOrWhiteSpace(document.FileHash))
            errors.Add("文件哈希不能为空");

        if (errors.Count > 0)
            throw new DocRetrievalValidationException(string.Join("; ", errors));
    }
}
