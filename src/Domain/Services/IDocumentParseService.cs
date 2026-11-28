using Doctheca.Domain.Models;

namespace Doctheca.Domain.Services;

public interface IDocumentParseService
{
    Task<DocumentParse> CreateAsync(Guid documentFileId, string modelVersion = "vlm");
    Task<DocumentParse?> GetByIdAsync(Guid id);
    Task<DocumentParse?> GetLatestByFileIdAsync(Guid documentFileId);
    Task<DocumentParse?> GetLatestByFileIdAndModelAsync(Guid documentFileId, string modelVersion);
    Task<DocumentParse> UpdateStatusAsync(
        Guid id,
        string status,
        string? errorMessage = null,
        string? externalTaskId = null,
        Guid? structaDocParseRunId = null);
    Task<List<DocumentParse>> GetByStatusAsync(string status);
    Task<List<DocumentParse>> GetActiveJobsAsync();
    Task<List<DocumentParseImage>> GetImagesByParseIdAsync(Guid parseId);
    Task<(List<DocumentParse> Items, int TotalCount)> GetListAsync(int page, int size, string? search = null);
    Task<string> CompleteAsync(Guid parseId, Guid runId, string markdown,
        IReadOnlyCollection<DocumentParsePage> pages, IReadOnlyList<DocumentParseImage> images,
        IReadOnlyList<DocumentParseBlock> blocks, CancellationToken ct = default);

    Task<bool> DeleteParseAsync(Guid parseId);
    Task<List<DocumentParse>> GetByFileIdAsync(Guid documentFileId);
}
