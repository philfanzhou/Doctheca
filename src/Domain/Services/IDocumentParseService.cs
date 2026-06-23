using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Domain.Services;

public interface IDocumentParseService
{
    Task<DocumentParseModel> CreateAsync(Guid documentFileId);
    Task<DocumentParseModel?> GetByIdAsync(Guid id);
    Task<DocumentParseModel?> GetLatestByFileIdAsync(Guid documentFileId);
    Task<DocumentParseModel> UpdateStatusAsync(Guid id, string status, string? errorMessage = null, string? markdownContent = null, string? externalTaskId = null);
    Task<List<DocumentParseModel>> GetPendingJobsAsync();
    Task AddImageAsync(DocumentParseImageModel image);
    Task<List<DocumentParseImageModel>> GetImagesByParseIdAsync(Guid parseId);
    Task<List<DocumentParseImageModel>> GetImagesByFileIdAsync(Guid documentFileId);
}
