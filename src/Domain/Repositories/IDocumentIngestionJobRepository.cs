using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Domain.Repositories;

public interface IDocumentIngestionJobRepository
{
    Task AddAsync(DocumentIngestionJobModel model);
    Task<DocumentIngestionJobModel?> GetByIdAsync(Guid id);
    Task<DocumentIngestionJobModel?> GetByDocumentIdAsync(Guid documentId);
    Task<List<DocumentIngestionJobModel>> GetByStatusAsync(string status);
    Task<bool> UpdateAsync(DocumentIngestionJobModel model);
}
