using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.DocLibrary.Database.Entities;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;

namespace Ruoyu.Study.DocLibrary.Database.Repositories;

public class DocumentFileRepository : IDocumentFileRepository
{
    private readonly DocLibraryDbContext _context;

    public DocumentFileRepository(DocLibraryDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(DocumentFileModel model)
    {
        var entity = MapToEntity(model);
        _context.DocumentFiles.Add(entity);
        await _context.SaveChangesAsync();
        model.Id = entity.Id;
    }

    public async Task<DocumentFileModel?> GetByIdAsync(Guid id)
    {
        var entity = await _context.DocumentFiles.FindAsync(id);
        return entity != null ? MapToModel(entity) : null;
    }

    public async Task<(List<DocumentFileModel> Items, int TotalCount)> GetListAsync(int page, int size, string? fileName = null)
    {
        var query = _context.DocumentFiles.AsQueryable();

        if (!string.IsNullOrWhiteSpace(fileName))
            query = query.Where(e => e.FileName.ToLower().Contains(fileName.ToLower()));

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(e => e.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(e => MapToModel(e))
            .ToListAsync();

        return (items, total);
    }

    public async Task<bool> UpdateAsync(DocumentFileModel model)
    {
        var entity = await _context.DocumentFiles.FindAsync(model.Id);
        if (entity == null) return false;

        entity.FileName = model.FileName;
        entity.FilePath = model.FilePath;
        entity.ContentType = model.ContentType;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var entity = await _context.DocumentFiles.FindAsync(id);
        if (entity == null) return false;
        _context.DocumentFiles.Remove(entity);
        await _context.SaveChangesAsync();
        return true;
    }

    private static DocumentFileEntity MapToEntity(DocumentFileModel model) => new()
    {
        Id = model.Id,
        FileName = model.FileName,
        FilePath = model.FilePath,
        ContentType = model.ContentType,
        CreatedBy = model.CreatedBy,
        CreatedAt = model.CreatedAt,
        UpdatedAt = model.UpdatedAt,
    };

    private static DocumentFileModel MapToModel(DocumentFileEntity entity) => new()
    {
        Id = entity.Id,
        FileName = entity.FileName,
        FilePath = entity.FilePath,
        ContentType = entity.ContentType,
        CreatedBy = entity.CreatedBy,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
    };
}
