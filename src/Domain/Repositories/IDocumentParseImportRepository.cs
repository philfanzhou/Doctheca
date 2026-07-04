using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Domain.Repositories;

public interface IDocumentParseImportRepository
{
    Task<DocumentParseImportModel?> GetByParseIdAsync(Guid parseId);
    Task<DocumentParseImportModel> AddAsync(DocumentParseImportModel model);
    Task UpdateAsync(DocumentParseImportModel model);
}
