using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Domain.Repositories;

public interface IDocumentFileImageRepository
{
    Task AddAsync(DocumentFileImageModel model);
    Task<List<DocumentFileImageModel>> GetByDocumentFileIdAsync(Guid documentFileId);
    Task DeleteByDocumentFileIdAsync(Guid documentFileId);
}
