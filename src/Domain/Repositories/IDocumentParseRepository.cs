using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Domain.Repositories;

public interface IDocumentParseRepository
{
    Task<DocumentParseModel> AddAsync(DocumentParseModel model);
    Task<DocumentParseModel?> GetByIdAsync(Guid id);
    Task<DocumentParseModel?> GetLatestByFileIdAsync(Guid documentFileId);
    Task<DocumentParseModel> UpdateAsync(DocumentParseModel model);
    Task<List<DocumentParseModel>> GetByStatusAsync(string status);
    Task<(List<DocumentParseModel> Items, int TotalCount)> GetListAsync(int page, int size, string? search = null);
    Task DeleteAsync(Guid id);
}
