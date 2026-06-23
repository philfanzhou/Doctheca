using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.DocRetrieval.Database.Entities;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;

namespace Ruoyu.Study.DocRetrieval.Database.Repositories;

public class DocumentFileRepository : IDocumentFileRepository
{
    private readonly DocRetrievalDbContext _context;

    public DocumentFileRepository(DocRetrievalDbContext context)
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

    public async Task<(List<DocumentFileModel> Items, int TotalCount)> GetListAsync(int page, int size, string? status = null)
    {
        var query = _context.DocumentFiles.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(e => e.Status == status);

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
        entity.FileSize = model.FileSize;
        entity.ContentType = model.ContentType;
        entity.Status = model.Status;
        entity.ExternalTaskId = model.ExternalTaskId;
        entity.MarkdownContent = model.MarkdownContent;
        entity.ErrorMessage = model.ErrorMessage;
        entity.ParsedAt = model.ParsedAt;
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

    public async Task<List<DocumentFileModel>> GetByStatusAsync(string status)
    {
        return await _context.DocumentFiles
            .Where(e => e.Status == status)
            .Select(e => MapToModel(e))
            .ToListAsync();
    }

    private static DocumentFileEntity MapToEntity(DocumentFileModel model) => new()
    {
        Id = model.Id,
        FileName = model.FileName,
        FilePath = model.FilePath,
        FileSize = model.FileSize,
        ContentType = model.ContentType,
        Status = model.Status,
        ExternalTaskId = model.ExternalTaskId,
        MarkdownContent = model.MarkdownContent,
        ErrorMessage = model.ErrorMessage,
        ParsedAt = model.ParsedAt,
        CreatedBy = model.CreatedBy,
        CreatedAt = model.CreatedAt,
        UpdatedAt = model.UpdatedAt,
    };

    private static DocumentFileModel MapToModel(DocumentFileEntity entity) => new()
    {
        Id = entity.Id,
        FileName = entity.FileName,
        FilePath = entity.FilePath,
        FileSize = entity.FileSize,
        ContentType = entity.ContentType,
        Status = entity.Status,
        ExternalTaskId = entity.ExternalTaskId,
        MarkdownContent = entity.MarkdownContent,
        ErrorMessage = entity.ErrorMessage,
        ParsedAt = entity.ParsedAt,
        CreatedBy = entity.CreatedBy,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
    };
}
