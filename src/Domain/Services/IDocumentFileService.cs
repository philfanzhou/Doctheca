using Doctheca.Domain.Models;

namespace Doctheca.Domain.Services;

public interface IDocumentFileService
{
    Task<DocumentFile> CreateAsync(DocumentFile model);
    Task<DocumentFile?> GetByIdAsync(Guid id);
    Task<(List<DocumentFile> Items, int TotalCount)> GetListAsync(int page, int size, string? fileName = null);
    Task<DocumentFile?> AttachStructaDocDocumentAsync(Guid id, Guid structaDocDocumentId);
    Task<IReadOnlyList<string>?> DeleteWithAudioAsync(Guid id);
}
