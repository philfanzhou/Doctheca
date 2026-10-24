using Doctheca.Domain.Models;

namespace Doctheca.Domain.Repositories;

public interface IDocumentParseRepository
{
    Task<DocumentParseModel> AddAsync(DocumentParseModel model);
    Task<DocumentParseModel?> GetByIdAsync(Guid id);
    Task<DocumentParseModel?> GetLatestByFileIdAsync(Guid documentFileId);
    Task<DocumentParseModel?> GetLatestByFileIdAndModelAsync(Guid documentFileId, string modelVersion);
    Task<List<DocumentParseModel>> GetByFileIdAsync(Guid documentFileId);
    Task<DocumentParseModel> UpdateAsync(DocumentParseModel model);
    Task<List<DocumentParseModel>> GetByStatusAsync(string status);
    Task<List<DocumentParseModel>> GetByStatusesAsync(IReadOnlyCollection<string> statuses);
    Task<(List<DocumentParseModel> Items, int TotalCount)> GetListAsync(int page, int size, string? search = null);
    Task<string> CompleteAsync(Guid parseId, Guid runId, string markdown,
        IReadOnlyCollection<DocumentParsePageModel> pages, IReadOnlyList<DocumentParseImageModel> images,
        IReadOnlyList<DocumentParseBlockModel> blocks, CancellationToken ct = default);

    Task DeleteAsync(Guid id);
}
