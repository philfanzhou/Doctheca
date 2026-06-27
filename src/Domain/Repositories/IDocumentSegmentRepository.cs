using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Domain.Repositories;

public interface IDocumentSegmentRepository
{
    Task AddAsync(DocumentSegmentModel model);
    Task AddRangeAsync(IEnumerable<DocumentSegmentModel> models);
    Task<List<DocumentSegmentModel>> GetByDocumentIdAsync(Guid documentId);
    Task DeleteByDocumentIdAsync(Guid documentId);
    Task<(List<DocumentSegmentModel> Items, int TotalCount)> SearchByTextAsync(string query, int pageSize, int skip, string? status = null, string? subject = null, string? grade = null, string? year = null);
}
