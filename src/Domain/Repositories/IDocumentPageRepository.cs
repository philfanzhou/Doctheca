using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Domain.Repositories;

public interface IDocumentPageRepository
{
    Task AddAsync(DocumentPageModel model);
    Task AddRangeAsync(IEnumerable<DocumentPageModel> models);
    Task<List<DocumentPageModel>> GetByDocumentIdAsync(Guid documentId);
    Task DeleteByDocumentIdAsync(Guid documentId);
}
