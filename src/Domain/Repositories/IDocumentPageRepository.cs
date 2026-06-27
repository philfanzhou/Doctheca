using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Domain.Repositories;

public interface IDocumentPageRepository
{
    Task AddAsync(DocumentPageModel model);
    Task AddRangeAsync(IEnumerable<DocumentPageModel> models);
    Task<List<DocumentPageModel>> GetByDocumentIdAsync(Guid documentId);
    Task DeleteByDocumentIdAsync(Guid documentId);
}
