using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Domain.Repositories;

public interface IDocumentRepository
{
    Task AddAsync(DocumentModel model);
    Task<DocumentModel?> GetByIdAsync(Guid id);
    Task<DocumentModel?> GetByTitleAsync(string title);
    Task<DocumentModel?> GetByFileHashAsync(string fileHash);
    Task<DocumentModel?> GetByFileHashAndStatusAsync(string fileHash, string status);
    Task<bool> UpdateAsync(DocumentModel model);
    Task<bool> DeleteAsync(Guid id);
    Task<(List<DocumentModel> Items, int TotalCount)> GetListAsync(int page, int size, string? status = null, string? subject = null, string? grade = null, string? keyword = null, string? year = null);
}

public interface IDocumentPageRepository
{
    Task AddAsync(DocumentPageModel model);
    Task AddRangeAsync(IEnumerable<DocumentPageModel> models);
    Task<List<DocumentPageModel>> GetByDocumentIdAsync(Guid documentId);
    Task DeleteByDocumentIdAsync(Guid documentId);
}

public interface IDocumentSegmentRepository
{
    Task AddAsync(DocumentSegmentModel model);
    Task AddRangeAsync(IEnumerable<DocumentSegmentModel> models);
    Task<List<DocumentSegmentModel>> GetByDocumentIdAsync(Guid documentId);
    Task DeleteByDocumentIdAsync(Guid documentId);
}

public interface IQuestionSegmentRepository
{
    Task AddAsync(QuestionSegmentModel model);
    Task AddRangeAsync(IEnumerable<QuestionSegmentModel> models);
    Task<List<QuestionSegmentModel>> GetByDocumentIdAsync(Guid documentId);
    Task DeleteByDocumentIdAsync(Guid documentId);
}

public interface IDocumentOccurrenceRepository
{
    Task AddAsync(DocumentOccurrenceModel model);
    Task AddRangeAsync(IEnumerable<DocumentOccurrenceModel> models);
    Task<List<DocumentOccurrenceModel>> GetByDocumentIdAndTokenAsync(Guid documentId, string tokenText);
    Task DeleteByDocumentIdAsync(Guid documentId);
}

public interface IDocumentIngestionJobRepository
{
    Task AddAsync(DocumentIngestionJobModel model);
    Task<DocumentIngestionJobModel?> GetByIdAsync(Guid id);
    Task<DocumentIngestionJobModel?> GetByDocumentIdAsync(Guid documentId);
    Task<List<DocumentIngestionJobModel>> GetByStatusAsync(string status);
    Task<bool> UpdateAsync(DocumentIngestionJobModel model);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync();
}
