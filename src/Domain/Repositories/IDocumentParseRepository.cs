using Doctheca.Domain.Models;

namespace Doctheca.Domain.Repositories;

public interface IDocumentParseRepository
{
    Task<DocumentParse> AddAsync(DocumentParse model);
    Task<DocumentParse?> GetByIdAsync(Guid id);
    Task<DocumentParse?> GetLatestByFileIdAsync(Guid documentFileId);
    Task<DocumentParse?> GetLatestByFileIdAndModelAsync(Guid documentFileId, string modelVersion);
    Task<List<DocumentParse>> GetByFileIdAsync(Guid documentFileId);
    Task<DocumentParse> UpdateAsync(DocumentParse model);
    Task<List<DocumentParse>> GetByStatusAsync(string status);
    Task<List<DocumentParse>> GetByStatusesAsync(IReadOnlyCollection<string> statuses);
    Task<(List<DocumentParse> Items, int TotalCount)> GetListAsync(int page, int size, string? search = null);
    Task<string> CompleteAsync(Guid parseId, Guid runId, string markdown,
        IReadOnlyCollection<DocumentParsePage> pages, IReadOnlyList<DocumentParseImage> images,
        IReadOnlyList<DocumentParseBlock> blocks, CancellationToken ct = default);

    Task DeleteAsync(Guid id);
}
