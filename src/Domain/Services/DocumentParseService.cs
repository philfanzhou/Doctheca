using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;

namespace Ruoyu.Study.DocLibrary.Domain.Services;

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

    public async Task<DocumentParseModel> CreateAsync(Guid documentFileId, string modelVersion = "vlm")
    {
        var model = new DocumentParseModel
        {
            DocumentFileId = documentFileId,
            ModelVersion = modelVersion,
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

    public async Task<DocumentParseModel?> GetLatestByFileIdAndModelAsync(Guid documentFileId, string modelVersion)
    {
        return await _parseRepository.GetLatestByFileIdAndModelAsync(documentFileId, modelVersion);
    }

    public async Task<DocumentParseModel> UpdateStatusAsync(
        Guid id,
        string status,
        string? errorMessage = null,
        string? markdownContent = null,
        string? externalTaskId = null,
        string? contentList = null,
        string? contentListV2 = null,
        string? modelJson = null,
        string? layoutJson = null,
        string? zipPath = null,
        Guid? structaDocParseRunId = null)
    {
        var model = await _parseRepository.GetByIdAsync(id)
            ?? throw new KeyNotFoundException($"Document parse not found: {id}");

        model.Status = status;

        if (errorMessage != null) model.ErrorMessage = errorMessage;
        if (markdownContent != null) model.MarkdownContent = markdownContent;
        if (externalTaskId != null) model.ExternalTaskId = externalTaskId;
        if (structaDocParseRunId != null) model.StructaDocParseRunId = structaDocParseRunId;
        if (contentList != null) model.ContentList = contentList;
        if (contentListV2 != null) model.ContentListV2 = contentListV2;
        if (modelJson != null) model.ModelJson = modelJson;
        if (layoutJson != null) model.LayoutJson = layoutJson;
        if (zipPath != null) model.ZipPath = zipPath;
        if (status == DocumentParseStatus.Parsed) model.ParsedAt = DateTimeOffset.UtcNow;

        var result = await _parseRepository.UpdateAsync(model);
        _logger.LogInformation("Document parse status updated: {Id}, Status={Status}", id, status);
        return result;
    }

    public async Task<List<DocumentParseModel>> GetPendingJobsAsync()
    {
        return await _parseRepository.GetByStatusAsync(DocumentParseStatus.Pending);
    }

    public async Task<List<DocumentParseModel>> GetActiveJobsAsync()
    {
        return await _parseRepository.GetByStatusesAsync([DocumentParseStatus.Pending, DocumentParseStatus.Parsing]);
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

    public async Task<(List<DocumentParseModel> Items, int TotalCount)> GetListAsync(int page, int size, string? search = null)
    {
        return await _parseRepository.GetListAsync(page, size, search);
    }

    public async Task<bool> DeleteParseAsync(Guid parseId)
    {
        var model = await _parseRepository.GetByIdAsync(parseId);
        if (model == null) return false;

        await _imageRepository.DeleteByParseIdAsync(parseId);
        await _parseRepository.DeleteAsync(parseId);

        _logger.LogInformation("Document parse deleted: {ParseId}", parseId);
        return true;
    }

    public async Task<List<DocumentParseModel>> GetByFileIdAsync(Guid documentFileId)
    {
        return await _parseRepository.GetByFileIdAsync(documentFileId);
    }
}
