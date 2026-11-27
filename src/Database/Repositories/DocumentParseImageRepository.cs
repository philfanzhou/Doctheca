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

    public async Task<List<DocumentParseImage>> GetByParseIdAsync(Guid parseId)
    {
        return await _context.DocumentParseImages
            .Where(e => e.ParseId == parseId)
            .Select(e => MapToModel(e))
            .ToListAsync();
    }

    internal static DocumentParseImageEntity MapToEntity(DocumentParseImage model) => new()
    {
        Id = model.Id,
        ParseId = model.ParseId,
        ImageName = model.ImageName,
        ImagePath = model.ImagePath,
        ContentType = model.ContentType,
    };

    private static DocumentParseImage MapToModel(DocumentParseImageEntity entity) => new()
    {
        Id = entity.Id,
        ParseId = entity.ParseId,
        ImageName = entity.ImageName,
        ImagePath = entity.ImagePath,
        ContentType = entity.ContentType,
    };
}
