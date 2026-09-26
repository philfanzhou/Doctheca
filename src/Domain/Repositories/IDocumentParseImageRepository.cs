using Doctheca.Domain.Models;

namespace Doctheca.Domain.Repositories;

public interface IDocumentParseImageRepository
{
    Task<DocumentParseImageModel> AddAsync(DocumentParseImageModel image);
    Task<List<DocumentParseImageModel>> GetByParseIdAsync(Guid parseId);
    Task<List<DocumentParseImageModel>> GetByFileIdAsync(Guid documentFileId);
    Task DeleteByParseIdAsync(Guid parseId);
}
