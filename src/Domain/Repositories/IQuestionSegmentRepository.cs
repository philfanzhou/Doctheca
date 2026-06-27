using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Domain.Repositories;

public interface IQuestionSegmentRepository
{
    Task AddAsync(QuestionSegmentModel model);
    Task AddRangeAsync(IEnumerable<QuestionSegmentModel> models);
    Task<List<QuestionSegmentModel>> GetByDocumentIdAsync(Guid documentId);
    Task DeleteByDocumentIdAsync(Guid documentId);
    Task<(List<QuestionSegmentModel> Items, int TotalCount)> SearchByStemAsync(string query, int pageSize, int skip, string? status = null, string? subject = null, string? grade = null, string? year = null);
}
