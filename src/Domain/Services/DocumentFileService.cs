using Microsoft.Extensions.Logging;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;

namespace Doctheca.Domain.Services;

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

    public async Task<DocumentFile> CreateAsync(DocumentFile model)
    {
        model.CreatedAt = DateTimeOffset.UtcNow;
        await _fileRepository.AddAsync(model);
        _logger.LogInformation("Document file created: {Id}, FileName={FileName}", model.Id, model.FileName);
        return model;
    }

    public async Task<DocumentFile?> GetByIdAsync(Guid id)
    {
        return await _fileRepository.GetByIdAsync(id);
    }

    public async Task<(List<DocumentFile> Items, int TotalCount)> GetListAsync(int page, int size, string? fileName = null)
    {
        return await _fileRepository.GetListAsync(page, size, fileName);
    }

    public async Task<DocumentFile?> AttachStructaDocDocumentAsync(Guid id, Guid structaDocDocumentId)
    {
        var updated = await _fileRepository.AttachStructaDocDocumentAsync(id, structaDocDocumentId);
        if (!updated)
        {
            _logger.LogWarning("Document file not found for StructaDoc attach: {Id}", id);
            return null;
        }

        return await _fileRepository.GetByIdAsync(id);
    }

    public Task<IReadOnlyList<string>?> DeleteWithAudioAsync(Guid id) => _fileRepository.DeleteWithAudioAsync(id);
}
