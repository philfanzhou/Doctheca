using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.DocLibrary.Database.Entities;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;

namespace Ruoyu.Study.DocLibrary.Database.Repositories;

public class QuestionSegmentRepository : IQuestionSegmentRepository
{
    private readonly DocLibraryDbContext _dbContext;

    public QuestionSegmentRepository(DocLibraryDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(QuestionSegmentModel model)
    {
        await _dbContext.QuestionSegments.AddAsync(MapToEntity(model));
    }

    public async Task AddRangeAsync(IEnumerable<QuestionSegmentModel> models)
    {
        await _dbContext.QuestionSegments.AddRangeAsync(models.Select(MapToEntity));
    }

    public async Task<List<QuestionSegmentModel>> GetByDocumentIdAsync(Guid documentId)
    {
        return await _dbContext.QuestionSegments
            .Where(q => q.DocumentId == documentId)
            .OrderBy(q => q.PageId).ThenBy(q => q.StartOffset)
            .Select(q => MapToModel(q))
            .ToListAsync();
    }

    public async Task DeleteByDocumentIdAsync(Guid documentId)
    {
        var entities = await _dbContext.QuestionSegments
            .Where(q => q.DocumentId == documentId)
            .ToListAsync();
        _dbContext.QuestionSegments.RemoveRange(entities);
    }

    public async Task<(List<QuestionSegmentModel> Items, int TotalCount)> SearchByStemAsync(string query, int pageSize, int skip, string? status = null, string? subject = null, string? grade = null, string? year = null)
    {
        var questionQuery = from q in _dbContext.QuestionSegments
                            join doc in _dbContext.Documents on q.DocumentId equals doc.Id
                            where q.Stem.Contains(query)
                            where doc.Status == (status ?? DocumentStatus.Ready)
                            select new { Question = q, Document = doc };

        if (!string.IsNullOrWhiteSpace(subject))
            questionQuery = questionQuery.Where(x => x.Document.Subject == subject);
        if (!string.IsNullOrWhiteSpace(grade))
            questionQuery = questionQuery.Where(x => x.Document.Grade == grade);
        if (!string.IsNullOrWhiteSpace(year))
            questionQuery = questionQuery.Where(x => x.Document.Year == year);

        var totalCount = await questionQuery.CountAsync();
        var items = await questionQuery
            .OrderByDescending(x => x.Question.CreatedAt)
            .Skip(skip)
            .Take(pageSize)
            .Select(x => x.Question)
            .ToListAsync();

        return (items.Select(MapToModel).ToList(), totalCount);
    }

    private static QuestionSegmentEntity MapToEntity(QuestionSegmentModel model) => new()
    {
        Id = model.Id,
        DocumentId = model.DocumentId,
        PageId = model.PageId,
        QuestionId = model.QuestionId,
        Stem = model.Stem,
        OptionsJson = model.OptionsJson,
        AnswerArea = model.AnswerArea,
        StartOffset = model.StartOffset,
        EndOffset = model.EndOffset,
        CreatedAt = model.CreatedAt
    };

    private static QuestionSegmentModel MapToModel(QuestionSegmentEntity entity) => new()
    {
        Id = entity.Id,
        DocumentId = entity.DocumentId,
        PageId = entity.PageId,
        QuestionId = entity.QuestionId,
        Stem = entity.Stem,
        OptionsJson = entity.OptionsJson,
        AnswerArea = entity.AnswerArea,
        StartOffset = entity.StartOffset,
        EndOffset = entity.EndOffset,
        CreatedAt = entity.CreatedAt
    };
}
