using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.DocRetrieval.Database.Entities;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;

namespace Ruoyu.Study.DocRetrieval.Database.Repositories;

public class DocumentIngestionJobRepository : IDocumentIngestionJobRepository
{
    private readonly DocRetrievalDbContext _dbContext;

    public DocumentIngestionJobRepository(DocRetrievalDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(DocumentIngestionJobModel model)
    {
        await _dbContext.DocumentIngestionJobs.AddAsync(MapToEntity(model));
    }

    public async Task<DocumentIngestionJobModel?> GetByIdAsync(Guid id)
    {
        var entity = await _dbContext.DocumentIngestionJobs.FindAsync(id);
        return entity != null ? MapToModel(entity) : null;
    }

    public async Task<DocumentIngestionJobModel?> GetByDocumentIdAsync(Guid documentId)
    {
        var entity = await _dbContext.DocumentIngestionJobs
            .FirstOrDefaultAsync(j => j.DocumentId == documentId);
        return entity != null ? MapToModel(entity) : null;
    }

    public async Task<List<DocumentIngestionJobModel>> GetByStatusAsync(string status)
    {
        return await _dbContext.DocumentIngestionJobs
            .Where(j => j.Status == status)
            .OrderBy(j => j.CreatedAt)
            .Select(j => MapToModel(j))
            .ToListAsync();
    }

    public async Task<bool> UpdateAsync(DocumentIngestionJobModel model)
    {
        var entity = await _dbContext.DocumentIngestionJobs.FindAsync(model.Id);
        if (entity == null) return false;

        entity.Status = model.Status;
        entity.ParserVersion = model.ParserVersion;
        entity.OcrVersion = model.OcrVersion;
        entity.ErrorMessage = model.ErrorMessage;
        entity.Progress = model.Progress;
        entity.ProgressStage = model.ProgressStage;
        entity.StartedAt = model.StartedAt;
        entity.FinishedAt = model.FinishedAt;

        _dbContext.DocumentIngestionJobs.Update(entity);
        return true;
    }

    public async Task DeleteByDocumentIdAsync(Guid documentId)
    {
        var entities = await _dbContext.DocumentIngestionJobs
            .Where(j => j.DocumentId == documentId)
            .ToListAsync();
        _dbContext.DocumentIngestionJobs.RemoveRange(entities);
    }

    private static DocumentIngestionJobEntity MapToEntity(DocumentIngestionJobModel model) => new()
    {
        Id = model.Id,
        DocumentId = model.DocumentId,
        Status = model.Status,
        ParserVersion = model.ParserVersion,
        OcrVersion = model.OcrVersion,
        ErrorMessage = model.ErrorMessage,
        Progress = model.Progress,
        ProgressStage = model.ProgressStage,
        StartedAt = model.StartedAt,
        FinishedAt = model.FinishedAt,
        CreatedAt = model.CreatedAt
    };

    private static DocumentIngestionJobModel MapToModel(DocumentIngestionJobEntity entity) => new()
    {
        Id = entity.Id,
        DocumentId = entity.DocumentId,
        Status = entity.Status,
        ParserVersion = entity.ParserVersion,
        OcrVersion = entity.OcrVersion,
        ErrorMessage = entity.ErrorMessage,
        Progress = entity.Progress,
        ProgressStage = entity.ProgressStage,
        StartedAt = entity.StartedAt,
        FinishedAt = entity.FinishedAt,
        CreatedAt = entity.CreatedAt
    };
}
