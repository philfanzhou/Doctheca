using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;

namespace Ruoyu.Study.DocRetrieval.Domain.Services;

public class DocumentFileService : IDocumentFileService
{
    private readonly IDocumentFileRepository _fileRepository;
    private readonly IDocumentFileImageRepository _imageRepository;
    private readonly ILogger<DocumentFileService> _logger;

    public DocumentFileService(
        IDocumentFileRepository fileRepository,
        IDocumentFileImageRepository imageRepository,
        ILogger<DocumentFileService> logger)
    {
        _fileRepository = fileRepository;
        _imageRepository = imageRepository;
        _logger = logger;
    }

    public async Task<DocumentFileModel> CreateAsync(DocumentFileModel model)
    {
        model.Status = DocumentFileStatus.Uploaded;
        model.CreatedAt = DateTimeOffset.UtcNow;
        await _fileRepository.AddAsync(model);
        _logger.LogInformation("Document file created: {Id}, FileName={FileName}", model.Id, model.FileName);
        return model;
    }

    public async Task<DocumentFileModel?> GetByIdAsync(Guid id)
    {
        return await _fileRepository.GetByIdAsync(id);
    }

    public async Task<(List<DocumentFileModel> Items, int TotalCount)> GetListAsync(int page, int size, string? status = null)
    {
        return await _fileRepository.GetListAsync(page, size, status);
    }

    public async Task<DocumentFileModel> UpdateStatusAsync(Guid id, string status, string? errorMessage = null, string? markdownContent = null, string? externalTaskId = null)
    {
        var model = await _fileRepository.GetByIdAsync(id)
            ?? throw new KeyNotFoundException($"Document file not found: {id}");

        model.Status = status;
        model.UpdatedAt = DateTimeOffset.UtcNow;

        if (errorMessage != null) model.ErrorMessage = errorMessage;
        if (markdownContent != null) model.MarkdownContent = markdownContent;
        if (externalTaskId != null) model.ExternalTaskId = externalTaskId;
        if (status == DocumentFileStatus.Parsed) model.ParsedAt = DateTimeOffset.UtcNow;

        await _fileRepository.UpdateAsync(model);
        _logger.LogInformation("Document file status updated: {Id}, Status={Status}", id, status);
        return model;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        await _imageRepository.DeleteByDocumentFileIdAsync(id);
        return await _fileRepository.DeleteAsync(id);
    }

    public async Task<List<DocumentFileModel>> GetPendingParseJobsAsync()
    {
        return await _fileRepository.GetByStatusAsync(DocumentFileStatus.PendingParse);
    }

    public async Task AddImageAsync(DocumentFileImageModel image)
    {
        await _imageRepository.AddAsync(image);
    }

    public async Task<List<DocumentFileImageModel>> GetImagesByFileIdAsync(Guid documentFileId)
    {
        return await _imageRepository.GetByDocumentFileIdAsync(documentFileId);
    }
}
