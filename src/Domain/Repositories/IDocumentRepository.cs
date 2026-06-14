using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Domain.Repositories;

public interface IDocumentRepository
{
    Task AddAsync(DocumentModel model);
    Task<DocumentModel?> GetByIdAsync(Guid id);
    Task<DocumentModel?> GetByTitleAsync(string title);
    Task<DocumentModel?> GetByFileHashAsync(string fileHash);
    Task<DocumentModel?> GetByFileHashAndStatusAsync(string fileHash, string status);
    Task<bool> UpdateAsync(DocumentModel model);
    Task<bool> DeleteAsync(Guid id);
    Task<(List<DocumentModel> Items, int TotalCount)> GetListAsync(int page, int size, string? status = null, string? subject = null, string? grade = null, string? keyword = null, string? year = null);
}
