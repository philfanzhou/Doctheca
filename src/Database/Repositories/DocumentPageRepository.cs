using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.DocLibrary.Database.Entities;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;

namespace Ruoyu.Study.DocLibrary.Database.Repositories;

public class DocumentPageRepository : IDocumentPageRepository
{
    private readonly DocLibraryDbContext _dbContext;

    public DocumentPageRepository(DocLibraryDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(DocumentPageModel model)
    {
        await _dbContext.DocumentPages.AddAsync(MapToEntity(model));
    }

    public async Task AddRangeAsync(IEnumerable<DocumentPageModel> models)
    {
        await _dbContext.DocumentPages.AddRangeAsync(models.Select(MapToEntity));
    }

    public async Task<List<DocumentPageModel>> GetByDocumentIdAsync(Guid documentId)
    {
        return await _dbContext.DocumentPages
            .Where(p => p.DocumentId == documentId)
            .OrderBy(p => p.PageNumber)
            .Select(p => MapToModel(p))
            .ToListAsync();
    }

    public async Task DeleteByDocumentIdAsync(Guid documentId)
    {
        var entities = await _dbContext.DocumentPages
            .Where(p => p.DocumentId == documentId)
            .ToListAsync();
        _dbContext.DocumentPages.RemoveRange(entities);
    }

    private static DocumentPageEntity MapToEntity(DocumentPageModel model) => new()
    {
        Id = model.Id,
        DocumentId = model.DocumentId,
        PageNumber = model.PageNumber,
        ImagePath = model.ImagePath,
        CreatedAt = model.CreatedAt
    };

    private static DocumentPageModel MapToModel(DocumentPageEntity entity) => new()
    {
        Id = entity.Id,
        DocumentId = entity.DocumentId,
        PageNumber = entity.PageNumber,
        ImagePath = entity.ImagePath,
        CreatedAt = entity.CreatedAt
    };
}
