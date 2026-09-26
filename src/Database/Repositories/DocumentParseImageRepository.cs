using Microsoft.EntityFrameworkCore;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;

namespace Doctheca.Database.Repositories;

public class DocumentParseImageRepository : IDocumentParseImageRepository
{
    private readonly DocthecaDbContext _context;

    public DocumentParseImageRepository(DocthecaDbContext context)
    {
        _context = context;
    }

    public async Task<DocumentParseImageModel> AddAsync(DocumentParseImageModel image)
    {
        var entity = MapToEntity(image);
        _context.DocumentParseImages.Add(entity);
        await _context.SaveChangesAsync();
        image.Id = entity.Id;
        return image;
    }

    public async Task<List<DocumentParseImageModel>> GetByParseIdAsync(Guid parseId)
    {
        return await _context.DocumentParseImages
            .Where(e => e.ParseId == parseId)
            .Select(e => MapToModel(e))
            .ToListAsync();
    }

    public async Task<List<DocumentParseImageModel>> GetByFileIdAsync(Guid documentFileId)
    {
        return await _context.DocumentParseImages
            .Where(e => e.Parse!.DocumentFileId == documentFileId)
            .Select(e => MapToModel(e))
            .ToListAsync();
    }

    public async Task DeleteByParseIdAsync(Guid parseId)
    {
        var images = await _context.DocumentParseImages
            .Where(e => e.ParseId == parseId)
            .ToListAsync();
        _context.DocumentParseImages.RemoveRange(images);
        await _context.SaveChangesAsync();
    }

    private static DocumentParseImageEntity MapToEntity(DocumentParseImageModel model) => new()
    {
        Id = model.Id,
        ParseId = model.ParseId,
        ImageName = model.ImageName,
        ImagePath = model.ImagePath,
        ContentType = model.ContentType,
    };

    private static DocumentParseImageModel MapToModel(DocumentParseImageEntity entity) => new()
    {
        Id = entity.Id,
        ParseId = entity.ParseId,
        ImageName = entity.ImageName,
        ImagePath = entity.ImagePath,
        ContentType = entity.ContentType,
    };
}
