using Doctheca.Domain.Models;

namespace Doctheca.Domain.Repositories;

public interface IDocumentFileRepository
{
    Task AddAsync(DocumentFileModel model);
    Task<DocumentFileModel?> GetByIdAsync(Guid id);
    Task<(List<DocumentFileModel> Items, int TotalCount)> GetListAsync(int page, int size, string? fileName = null);
    Task<bool> UpdateAsync(DocumentFileModel model);
    Task<bool> UpdateMetadataAsync(Guid id, string? subject, string? grade, string? year);
    Task<bool> AttachStructaDocDocumentAsync(Guid id, Guid structaDocDocumentId);
    /// <summary>Sets (or clears with null) the structure definition the document uses.</summary>
    Task<bool> SetStructureDefinitionAsync(Guid id, Guid? structureDefinitionId);
    Task<bool> DeleteAsync(Guid id);
}
