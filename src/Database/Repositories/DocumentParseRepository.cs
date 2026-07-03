using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.DocLibrary.Database.Entities;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;

namespace Ruoyu.Study.DocLibrary.Database.Repositories;

public class DocumentParseRepository : IDocumentParseRepository
{
    private readonly DocLibraryDbContext _context;

    public DocumentParseRepository(DocLibraryDbContext context)
    {
        _context = context;
    }

    public async Task<DocumentParseModel> AddAsync(DocumentParseModel model)
    {
        var entity = MapToEntity(model);
        _context.DocumentParses.Add(entity);
        await _context.SaveChangesAsync();
        model.Id = entity.Id;
        return model;
    }

    public async Task<DocumentParseModel?> GetByIdAsync(Guid id)
    {
        var entity = await _context.DocumentParses.FindAsync(id);
        return entity != null ? MapToModel(entity) : null;
    }

    public async Task<DocumentParseModel?> GetLatestByFileIdAsync(Guid documentFileId)
    {
        var entity = await _context.DocumentParses
            .Where(e => e.DocumentFileId == documentFileId)
            .OrderByDescending(e => e.Id)
            .FirstOrDefaultAsync();
        return entity != null ? MapToModel(entity) : null;
    }

    public async Task<DocumentParseModel> UpdateAsync(DocumentParseModel model)
    {
        var entity = await _context.DocumentParses.FindAsync(model.Id)
            ?? throw new KeyNotFoundException($"Document parse not found: {model.Id}");

        entity.Status = model.Status;
        entity.ModelVersion = model.ModelVersion;
        entity.ExternalTaskId = model.ExternalTaskId;
        entity.MarkdownContent = model.MarkdownContent;
        entity.ContentList = model.ContentList;
        entity.ZipPath = model.ZipPath;
        entity.LayoutPdfPath = model.LayoutPdfPath;
        entity.ErrorMessage = model.ErrorMessage;
        entity.ParsedAt = model.ParsedAt;

        await _context.SaveChangesAsync();
        return MapToModel(entity);
    }

    public async Task<List<DocumentParseModel>> GetByStatusAsync(string status)
    {
        return await _context.DocumentParses
            .Where(e => e.Status == status)
            .Select(e => MapToModel(e))
            .ToListAsync();
    }

    public async Task<(List<DocumentParseModel> Items, int TotalCount)> GetListAsync(int page, int size, string? search = null)
    {
        var query = _context.DocumentParses
            .Include(e => e.DocumentFile)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(e => e.DocumentFile != null && e.DocumentFile.FileName.Contains(search));
        }

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(e => e.ParsedAt)
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(e => MapToModel(e))
            .ToListAsync();

        return (items, totalCount);
    }

    public async Task DeleteAsync(Guid id)
    {
        var entity = await _context.DocumentParses.FindAsync(id);
        if (entity != null)
        {
            _context.DocumentParses.Remove(entity);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<DocumentParseModel?> GetLatestByFileIdAndModelAsync(Guid documentFileId, string modelVersion)
    {
        var entity = await _context.DocumentParses
            .Where(e => e.DocumentFileId == documentFileId && e.ModelVersion == modelVersion)
            .OrderByDescending(e => e.Id)
            .FirstOrDefaultAsync();
        return entity != null ? MapToModel(entity) : null;
    }

    public async Task<List<DocumentParseModel>> GetByFileIdAsync(Guid documentFileId)
    {
        return await _context.DocumentParses
            .Where(e => e.DocumentFileId == documentFileId)
            .OrderByDescending(e => e.Id)
            .Select(e => MapToModel(e))
            .ToListAsync();
    }

    private static DocumentParseEntity MapToEntity(DocumentParseModel model) => new()
    {
        Id = model.Id,
        DocumentFileId = model.DocumentFileId,
        ModelVersion = model.ModelVersion,
        Status = model.Status,
        ExternalTaskId = model.ExternalTaskId,
        MarkdownContent = model.MarkdownContent,
        ContentList = model.ContentList,
        ZipPath = model.ZipPath,
        LayoutPdfPath = model.LayoutPdfPath,
        ErrorMessage = model.ErrorMessage,
        ParsedAt = model.ParsedAt,
    };

    private static DocumentParseModel MapToModel(DocumentParseEntity entity) => new()
    {
        Id = entity.Id,
        DocumentFileId = entity.DocumentFileId,
        ModelVersion = entity.ModelVersion,
        Status = entity.Status,
        ExternalTaskId = entity.ExternalTaskId,
        MarkdownContent = entity.MarkdownContent,
        ContentList = entity.ContentList,
        ZipPath = entity.ZipPath,
        LayoutPdfPath = entity.LayoutPdfPath,
        ErrorMessage = entity.ErrorMessage,
        ParsedAt = entity.ParsedAt,
    };
}
