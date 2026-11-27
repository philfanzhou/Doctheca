using Doctheca.Domain.Models;

namespace Doctheca.Domain.Repositories;

public interface IDocumentFileRepository
{
    Task AddAsync(DocumentFile model);
    Task<DocumentFile?> GetByIdAsync(Guid id);
    Task<(List<DocumentFile> Items, int TotalCount)> GetListAsync(int page, int size, string? fileName = null);
    Task<bool> AttachStructaDocDocumentAsync(Guid id, Guid structaDocDocumentId);
    Task<IReadOnlyList<string>?> DeleteWithAudioAsync(Guid id);
}
