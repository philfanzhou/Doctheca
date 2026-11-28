using Microsoft.Extensions.Logging;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;

namespace Doctheca.Domain.Services;

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

    public async Task<DocumentParse> CreateAsync(Guid documentFileId, string modelVersion = "vlm")
    {
        var model = new DocumentParse
        {
            DocumentFileId = documentFileId,
            ModelVersion = modelVersion,
            Status = DocumentParseStatus.Pending,
        };
        var result = await _parseRepository.AddAsync(model);
        _logger.LogInformation("Document parse created: {Id}, FileId={FileId}", result.Id, documentFileId);
        return result;
    }

    public async Task<DocumentParse?> GetByIdAsync(Guid id)
    {
        return await _parseRepository.GetByIdAsync(id);
    }

    public async Task<DocumentParse?> GetLatestByFileIdAsync(Guid documentFileId)
    {
        return await _parseRepository.GetLatestByFileIdAsync(documentFileId);
    }

    public async Task<DocumentParse?> GetLatestByFileIdAndModelAsync(Guid documentFileId, string modelVersion)
    {
        return await _parseRepository.GetLatestByFileIdAndModelAsync(documentFileId, modelVersion);
    }

    public async Task<DocumentParse> UpdateStatusAsync(
        Guid id,
        string status,
        string? errorMessage = null,
        string? externalTaskId = null,
        Guid? structaDocParseRunId = null)
    {
        var model = await _parseRepository.GetByIdAsync(id)
            ?? throw new KeyNotFoundException($"Document parse not found: {id}");

        model.Status = status;

        if (errorMessage != null) model.ErrorMessage = errorMessage;
        if (externalTaskId != null) model.ExternalTaskId = externalTaskId;
        if (structaDocParseRunId != null) model.StructaDocParseRunId = structaDocParseRunId;
        if (status == DocumentParseStatus.Parsed) model.ParsedAt = DateTimeOffset.UtcNow;

        var result = await _parseRepository.UpdateAsync(model);
        _logger.LogInformation("Document parse status updated: {Id}, Status={Status}", id, status);
        return result;
    }

    public async Task<List<DocumentParse>> GetByStatusAsync(string status)
    {
        return await _parseRepository.GetByStatusAsync(status);
    }

    public async Task<List<DocumentParse>> GetActiveJobsAsync()
    {
        return await _parseRepository.GetByStatusesAsync([DocumentParseStatus.Pending, DocumentParseStatus.Parsing]);
    }

    public async Task<List<DocumentParseImage>> GetImagesByParseIdAsync(Guid parseId)
    {
        return await _imageRepository.GetByParseIdAsync(parseId);
    }

    public async Task<(List<DocumentParse> Items, int TotalCount)> GetListAsync(int page, int size, string? search = null)
    {
        return await _parseRepository.GetListAsync(page, size, search);
    }

    public Task<string> CompleteAsync(Guid parseId, Guid runId, string markdown,
        IReadOnlyCollection<DocumentParsePage> pages, IReadOnlyList<DocumentParseImage> images,
        IReadOnlyList<DocumentParseBlock> blocks, CancellationToken ct = default) =>
        _parseRepository.CompleteAsync(parseId, runId, markdown, pages, images, blocks, ct);

    public async Task<bool> DeleteParseAsync(Guid parseId)
    {
        var model = await _parseRepository.GetByIdAsync(parseId);
        if (model == null) return false;

        await _parseRepository.DeleteAsync(parseId);

        _logger.LogInformation("Document parse deleted: {ParseId}", parseId);
        return true;
    }

    public async Task<List<DocumentParse>> GetByFileIdAsync(Guid documentFileId)
    {
        return await _parseRepository.GetByFileIdAsync(documentFileId);
    }
}
