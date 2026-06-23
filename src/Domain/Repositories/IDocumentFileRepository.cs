using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Domain.Repositories;

public interface IDocumentFileRepository
{
    Task AddAsync(DocumentFileModel model);
    Task<DocumentFileModel?> GetByIdAsync(Guid id);
    Task<(List<DocumentFileModel> Items, int TotalCount)> GetListAsync(int page, int size, string? status = null);
    Task<bool> UpdateAsync(DocumentFileModel model);
    Task<bool> DeleteAsync(Guid id);
    Task<List<DocumentFileModel>> GetByStatusAsync(string status);
}
