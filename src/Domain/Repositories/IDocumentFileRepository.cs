using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Domain.Repositories;

public interface IDocumentFileRepository
{
    Task AddAsync(DocumentFileModel model);
    Task<DocumentFileModel?> GetByIdAsync(Guid id);
    Task<(List<DocumentFileModel> Items, int TotalCount)> GetListAsync(int page, int size, string? fileName = null);
    Task<bool> UpdateAsync(DocumentFileModel model);
    Task<bool> UpdateMetadataAsync(Guid id, string? subject, string? grade, string? year);
    Task<bool> AttachStructaDocDocumentAsync(Guid id, Guid structaDocDocumentId);
    Task<bool> DeleteAsync(Guid id);
}
