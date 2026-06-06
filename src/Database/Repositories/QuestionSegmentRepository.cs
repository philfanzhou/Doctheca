using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.DocRetrieval.Database.Entities;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;

namespace Ruoyu.Study.DocRetrieval.Database.Repositories;

public class QuestionSegmentRepository : IQuestionSegmentRepository
{
    private readonly DocRetrievalDbContext _dbContext;

    public QuestionSegmentRepository(DocRetrievalDbContext dbContext)
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
