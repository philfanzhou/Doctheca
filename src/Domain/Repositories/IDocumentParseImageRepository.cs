using Doctheca.Domain.Models;

namespace Doctheca.Domain.Repositories;

public interface IDocumentParseImageRepository
{
    Task<List<DocumentParseImage>> GetByParseIdAsync(Guid parseId);
}
