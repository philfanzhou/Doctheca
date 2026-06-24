using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.DocRetrieval.Database.Entities;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;

namespace Ruoyu.Study.DocRetrieval.Database.Repositories;

public class DocumentParseRepository : IDocumentParseRepository
{
    private readonly DocRetrievalDbContext _context;

    public DocumentParseRepository(DocRetrievalDbContext context)
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
        entity.ExternalTaskId = model.ExternalTaskId;
        entity.MarkdownContent = model.MarkdownContent;
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

    private static DocumentParseEntity MapToEntity(DocumentParseModel model) => new()
    {
        Id = model.Id,
        DocumentFileId = model.DocumentFileId,
        Status = model.Status,
        ExternalTaskId = model.ExternalTaskId,
        MarkdownContent = model.MarkdownContent,
        ErrorMessage = model.ErrorMessage,
        ParsedAt = model.ParsedAt,
    };

    private static DocumentParseModel MapToModel(DocumentParseEntity entity) => new()
    {
        Id = entity.Id,
        DocumentFileId = entity.DocumentFileId,
        Status = entity.Status,
        ExternalTaskId = entity.ExternalTaskId,
        MarkdownContent = entity.MarkdownContent,
        ErrorMessage = entity.ErrorMessage,
        ParsedAt = entity.ParsedAt,
    };
}
