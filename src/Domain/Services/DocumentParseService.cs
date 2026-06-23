using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;

namespace Ruoyu.Study.DocRetrieval.Domain.Services;

public class DocumentParseService : IDocumentParseService
{
    private readonly IDocumentParseRepository _parseRepository;
    private readonly IDocumentParseImageRepository _imageRepository;
    private readonly ILogger<DocumentParseService> _logger;

    public DocumentParseService(
        IDocumentParseRepository parseRepository,
        IDocumentParseImageRepository imageRepository,
        ILogger<DocumentParseService> logger)
    {
        _parseRepository = parseRepository;
        _imageRepository = imageRepository;
        _logger = logger;
    }

    public async Task<DocumentParseModel> CreateAsync(Guid documentFileId)
    {
        var model = new DocumentParseModel
        {
            DocumentFileId = documentFileId,
            Status = DocumentParseStatus.Pending,
        };
        var result = await _parseRepository.AddAsync(model);
        _logger.LogInformation("Document parse created: {Id}, FileId={FileId}", result.Id, documentFileId);
        return result;
    }

    public async Task<DocumentParseModel?> GetByIdAsync(Guid id)
    {
        return await _parseRepository.GetByIdAsync(id);
    }

    public async Task<DocumentParseModel?> GetLatestByFileIdAsync(Guid documentFileId)
    {
        return await _parseRepository.GetLatestByFileIdAsync(documentFileId);
    }

    public async Task<DocumentParseModel> UpdateStatusAsync(Guid id, string status, string? errorMessage = null, string? markdownContent = null, string? externalTaskId = null)
    {
        var model = await _parseRepository.GetByIdAsync(id)
            ?? throw new KeyNotFoundException($"Document parse not found: {id}");

        model.Status = status;

        if (errorMessage != null) model.ErrorMessage = errorMessage;
        if (markdownContent != null) model.MarkdownContent = markdownContent;
        if (externalTaskId != null) model.ExternalTaskId = externalTaskId;
        if (status == DocumentParseStatus.Parsed) model.ParsedAt = DateTimeOffset.UtcNow;

        var result = await _parseRepository.UpdateAsync(model);
        _logger.LogInformation("Document parse status updated: {Id}, Status={Status}", id, status);
        return result;
    }

    public async Task<List<DocumentParseModel>> GetPendingJobsAsync()
    {
        return await _parseRepository.GetByStatusAsync(DocumentParseStatus.Pending);
    }

    public async Task AddImageAsync(DocumentParseImageModel image)
    {
        await _imageRepository.AddAsync(image);
    }

    public async Task<List<DocumentParseImageModel>> GetImagesByParseIdAsync(Guid parseId)
    {
        return await _imageRepository.GetByParseIdAsync(parseId);
    }

    public async Task<List<DocumentParseImageModel>> GetImagesByFileIdAsync(Guid documentFileId)
    {
        return await _imageRepository.GetByFileIdAsync(documentFileId);
    }
}
