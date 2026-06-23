using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.DocRetrieval.Database.Entities;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;

namespace Ruoyu.Study.DocRetrieval.Database.Repositories;

public class DocumentFileImageRepository : IDocumentFileImageRepository
{
    private readonly DocRetrievalDbContext _context;

    public DocumentFileImageRepository(DocRetrievalDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(DocumentFileImageModel model)
    {
        var entity = MapToEntity(model);
        _context.DocumentFileImages.Add(entity);
        await _context.SaveChangesAsync();
        model.Id = entity.Id;
    }

    public async Task<List<DocumentFileImageModel>> GetByDocumentFileIdAsync(Guid documentFileId)
    {
        return await _context.DocumentFileImages
            .Where(e => e.DocumentFileId == documentFileId)
            .Select(e => MapToModel(e))
            .ToListAsync();
    }

    public async Task DeleteByDocumentFileIdAsync(Guid documentFileId)
    {
        var images = await _context.DocumentFileImages
            .Where(e => e.DocumentFileId == documentFileId)
            .ToListAsync();
        _context.DocumentFileImages.RemoveRange(images);
        await _context.SaveChangesAsync();
    }

    private static DocumentFileImageEntity MapToEntity(DocumentFileImageModel model) => new()
    {
        Id = model.Id,
        DocumentFileId = model.DocumentFileId,
        ImageName = model.ImageName,
        ImagePath = model.ImagePath,
        ContentType = model.ContentType,
        FileSize = model.FileSize,
        CreatedAt = model.CreatedAt,
    };

    private static DocumentFileImageModel MapToModel(DocumentFileImageEntity entity) => new()
    {
        Id = entity.Id,
        DocumentFileId = entity.DocumentFileId,
        ImageName = entity.ImageName,
        ImagePath = entity.ImagePath,
        ContentType = entity.ContentType,
        FileSize = entity.FileSize,
        CreatedAt = entity.CreatedAt,
    };
}
