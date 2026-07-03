using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Domain.Repositories;

public interface IDocumentParseRepository
{
    Task<DocumentParseModel> AddAsync(DocumentParseModel model);
    Task<DocumentParseModel?> GetByIdAsync(Guid id);
    Task<DocumentParseModel?> GetLatestByFileIdAsync(Guid documentFileId);
    Task<List<DocumentParseModel>> GetByFileIdAsync(Guid documentFileId);
    Task<DocumentParseModel> UpdateAsync(DocumentParseModel model);
    Task<List<DocumentParseModel>> GetByStatusAsync(string status);
    Task<(List<DocumentParseModel> Items, int TotalCount)> GetListAsync(int page, int size, string? search = null);
    Task DeleteAsync(Guid id);
}
