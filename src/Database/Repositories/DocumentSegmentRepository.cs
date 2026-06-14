using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.DocRetrieval.Database.Entities;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;

namespace Ruoyu.Study.DocRetrieval.Database.Repositories;

public class DocumentSegmentRepository : IDocumentSegmentRepository
{
    private readonly DocRetrievalDbContext _dbContext;

    public DocumentSegmentRepository(DocRetrievalDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(DocumentSegmentModel model)
    {
        await _dbContext.DocumentSegments.AddAsync(MapToEntity(model));
    }

    public async Task AddRangeAsync(IEnumerable<DocumentSegmentModel> models)
    {
        await _dbContext.DocumentSegments.AddRangeAsync(models.Select(MapToEntity));
    }

    public async Task<List<DocumentSegmentModel>> GetByDocumentIdAsync(Guid documentId)
    {
        return await _dbContext.DocumentSegments
            .Where(s => s.DocumentId == documentId)
            .OrderBy(s => s.PageId).ThenBy(s => s.StartOffset)
            .Select(s => MapToModel(s))
            .ToListAsync();
    }

    public async Task DeleteByDocumentIdAsync(Guid documentId)
    {
        var entities = await _dbContext.DocumentSegments
            .Where(s => s.DocumentId == documentId)
            .ToListAsync();
        _dbContext.DocumentSegments.RemoveRange(entities);
    }

    public async Task<(List<DocumentSegmentModel> Items, int TotalCount)> SearchByTextAsync(string query, int pageSize, int skip, string? status = null, string? subject = null, string? grade = null, string? year = null)
    {
        var segmentQuery = from seg in _dbContext.DocumentSegments
                           join doc in _dbContext.Documents on seg.DocumentId equals doc.Id
                           where seg.Text.Contains(query)
                           where doc.Status == (status ?? DocumentStatus.Ready)
                           select new { Segment = seg, Document = doc };

        if (!string.IsNullOrWhiteSpace(subject))
            segmentQuery = segmentQuery.Where(x => x.Document.Subject == subject);
        if (!string.IsNullOrWhiteSpace(grade))
            segmentQuery = segmentQuery.Where(x => x.Document.Grade == grade);
        if (!string.IsNullOrWhiteSpace(year))
            segmentQuery = segmentQuery.Where(x => x.Document.Year == year);

        var totalCount = await segmentQuery.CountAsync();
        var items = await segmentQuery
            .Skip(skip)
            .Take(pageSize)
            .Select(x => x.Segment)
            .ToListAsync();

        return (items.Select(MapToModel).ToList(), totalCount);
    }

    private static DocumentSegmentEntity MapToEntity(DocumentSegmentModel model) => new()
    {
        Id = model.Id,
        DocumentId = model.DocumentId,
        PageId = model.PageId,
        BlockId = model.BlockId,
        SentenceId = model.SentenceId,
        SegmentType = model.SegmentType,
        Text = model.Text,
        StartOffset = model.StartOffset,
        EndOffset = model.EndOffset,
        CreatedAt = model.CreatedAt
    };

    private static DocumentSegmentModel MapToModel(DocumentSegmentEntity entity) => new()
    {
        Id = entity.Id,
        DocumentId = entity.DocumentId,
        PageId = entity.PageId,
        BlockId = entity.BlockId,
        SentenceId = entity.SentenceId,
        SegmentType = entity.SegmentType,
        Text = entity.Text,
        StartOffset = entity.StartOffset,
        EndOffset = entity.EndOffset,
        CreatedAt = entity.CreatedAt
    };
}
