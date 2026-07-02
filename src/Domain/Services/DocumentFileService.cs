using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Services;

namespace Ruoyu.Study.DocLibrary.Domain.Services;

public class DocumentFileService : IDocumentFileService
{
    private readonly IDocumentFileRepository _fileRepository;
    private readonly ILogger<DocumentFileService> _logger;

    public DocumentFileService(
        IDocumentFileRepository fileRepository,
        ILogger<DocumentFileService> logger)
    {
        _fileRepository = fileRepository;
        _logger = logger;
    }

    public async Task<DocumentFileModel> CreateAsync(DocumentFileModel model)
    {
        model.CreatedAt = DateTimeOffset.UtcNow;
        await _fileRepository.AddAsync(model);
        _logger.LogInformation("Document file created: {Id}, FileName={FileName}", model.Id, model.FileName);
        return model;
    }

    public async Task<DocumentFileModel?> GetByIdAsync(Guid id)
    {
        return await _fileRepository.GetByIdAsync(id);
    }

    public async Task<(List<DocumentFileModel> Items, int TotalCount)> GetListAsync(int page, int size, string? fileName = null)
    {
        return await _fileRepository.GetListAsync(page, size, fileName);
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        return await _fileRepository.DeleteAsync(id);
    }
}
