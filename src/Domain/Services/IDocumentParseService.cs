using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Domain.Services;

public interface IDocumentParseService
{
    Task<DocumentParseModel> CreateAsync(Guid documentFileId);
    Task<DocumentParseModel?> GetByIdAsync(Guid id);
    Task<DocumentParseModel?> GetLatestByFileIdAsync(Guid documentFileId);
    Task<DocumentParseModel> UpdateStatusAsync(
        Guid id,
        string status,
        string? errorMessage = null,
        string? markdownContent = null,
        string? externalTaskId = null,
        string? contentList = null,
        string? zipPath = null,
        string? layoutPdfPath = null);
    Task<List<DocumentParseModel>> GetPendingJobsAsync();
    Task AddImageAsync(DocumentParseImageModel image);
    Task<List<DocumentParseImageModel>> GetImagesByParseIdAsync(Guid parseId);
    Task<List<DocumentParseImageModel>> GetImagesByFileIdAsync(Guid documentFileId);
    Task<(List<DocumentParseModel> Items, int TotalCount)> GetListAsync(int page, int size, string? search = null);
    Task<bool> DeleteParseAsync(Guid parseId);
    Task<List<DocumentParseModel>> GetByFileIdAsync(Guid documentFileId);
}
