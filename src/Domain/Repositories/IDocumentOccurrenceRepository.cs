using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Domain.Repositories;

public interface IDocumentOccurrenceRepository
{
    Task AddAsync(DocumentOccurrenceModel model);
    Task AddRangeAsync(IEnumerable<DocumentOccurrenceModel> models);
    Task<List<DocumentOccurrenceModel>> GetByDocumentIdAndTokenAsync(Guid documentId, string tokenText);
    Task DeleteByDocumentIdAsync(Guid documentId);
}
