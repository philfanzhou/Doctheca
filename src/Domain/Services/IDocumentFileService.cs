using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Domain.Services;

public interface IDocumentFileService
{
    Task<DocumentFileModel> CreateAsync(DocumentFileModel model);
    Task<DocumentFileModel?> GetByIdAsync(Guid id);
    Task<(List<DocumentFileModel> Items, int TotalCount)> GetListAsync(int page, int size, string? status = null);
    Task<DocumentFileModel> UpdateStatusAsync(Guid id, string status, string? errorMessage = null, string? markdownContent = null, string? externalTaskId = null);
    Task<bool> DeleteAsync(Guid id);
    Task<List<DocumentFileModel>> GetPendingParseJobsAsync();
    Task AddImageAsync(DocumentFileImageModel image);
    Task<List<DocumentFileImageModel>> GetImagesByFileIdAsync(Guid documentFileId);
}
