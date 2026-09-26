using Doctheca.Domain.Models;

namespace Doctheca.Domain.Services;

public interface IDocumentParseService
{
    Task<DocumentParseModel> CreateAsync(Guid documentFileId, string modelVersion = "vlm");
    Task<DocumentParseModel?> GetByIdAsync(Guid id);
    Task<DocumentParseModel?> GetLatestByFileIdAsync(Guid documentFileId);
    Task<DocumentParseModel?> GetLatestByFileIdAndModelAsync(Guid documentFileId, string modelVersion);
    Task<DocumentParseModel> UpdateStatusAsync(
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
        Guid? structaDocParseRunId = null);
    Task<List<DocumentParseModel>> GetPendingJobsAsync();
    Task<List<DocumentParseModel>> GetActiveJobsAsync();
    Task AddImageAsync(DocumentParseImageModel image);
    Task<List<DocumentParseImageModel>> GetImagesByParseIdAsync(Guid parseId);
    Task<List<DocumentParseImageModel>> GetImagesByFileIdAsync(Guid documentFileId);
    Task<(List<DocumentParseModel> Items, int TotalCount)> GetListAsync(int page, int size, string? search = null);
    Task<bool> DeleteParseAsync(Guid parseId);
    Task<List<DocumentParseModel>> GetByFileIdAsync(Guid documentFileId);
}
