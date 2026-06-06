using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.DocRetrieval.Database.Entities;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;

namespace Ruoyu.Study.DocRetrieval.Database.Repositories;

public class DocumentOccurrenceRepository : IDocumentOccurrenceRepository
{
    private readonly DocRetrievalDbContext _dbContext;

    public DocumentOccurrenceRepository(DocRetrievalDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(DocumentOccurrenceModel model)
    {
        await _dbContext.DocumentOccurrences.AddAsync(MapToEntity(model));
    }

    public async Task AddRangeAsync(IEnumerable<DocumentOccurrenceModel> models)
    {
        await _dbContext.DocumentOccurrences.AddRangeAsync(models.Select(MapToEntity));
    }

    public async Task<List<DocumentOccurrenceModel>> GetByDocumentIdAndTokenAsync(Guid documentId, string tokenText)
    {
        return await _dbContext.DocumentOccurrences
            .Where(o => o.DocumentId == documentId && o.TokenText == tokenText)
            .Select(o => MapToModel(o))
            .ToListAsync();
    }

    public async Task DeleteByDocumentIdAsync(Guid documentId)
    {
        var entities = await _dbContext.DocumentOccurrences
            .Where(o => o.DocumentId == documentId)
            .ToListAsync();
        _dbContext.DocumentOccurrences.RemoveRange(entities);
    }

    private static DocumentOccurrenceEntity MapToEntity(DocumentOccurrenceModel model) => new()
    {
        Id = model.Id,
        DocumentId = model.DocumentId,
        SegmentId = model.SegmentId,
        QuestionSegmentId = model.QuestionSegmentId,
        TokenText = model.TokenText,
        TokenStem = model.TokenStem,
        StartOffset = model.StartOffset,
        EndOffset = model.EndOffset,
        CreatedAt = model.CreatedAt
    };

    private static DocumentOccurrenceModel MapToModel(DocumentOccurrenceEntity entity) => new()
    {
        Id = entity.Id,
        DocumentId = entity.DocumentId,
        SegmentId = entity.SegmentId,
        QuestionSegmentId = entity.QuestionSegmentId,
        TokenText = entity.TokenText,
        TokenStem = entity.TokenStem,
        StartOffset = entity.StartOffset,
        EndOffset = entity.EndOffset,
        CreatedAt = entity.CreatedAt
    };
}
