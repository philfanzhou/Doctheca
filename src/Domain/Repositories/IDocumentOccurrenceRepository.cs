using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Domain.Repositories;

public interface IDocumentOccurrenceRepository
{
    Task AddAsync(DocumentOccurrenceModel model);
    Task AddRangeAsync(IEnumerable<DocumentOccurrenceModel> models);
    Task<List<DocumentOccurrenceModel>> GetByDocumentIdAndTokenAsync(Guid documentId, string tokenText);
    Task DeleteByDocumentIdAsync(Guid documentId);
}
