using Microsoft.EntityFrameworkCore;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;

namespace Doctheca.Database.Repositories;

public class DocumentParseRepository : IDocumentParseRepository
{
    private readonly DocthecaDbContext _context;

    public DocumentParseRepository(DocthecaDbContext context)
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
        entity.StructaDocParseRunId = model.StructaDocParseRunId;
        entity.MarkdownContent = model.MarkdownContent;
        entity.ContentList = model.ContentList;
        entity.ContentListV2 = model.ContentListV2;
        entity.ModelJson = model.ModelJson;
        entity.LayoutJson = model.LayoutJson;
        entity.ZipPath = model.ZipPath;
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

    public async Task<List<DocumentParseModel>> GetByStatusesAsync(IReadOnlyCollection<string> statuses)
    {
        return await _context.DocumentParses
            .Where(e => statuses.Contains(e.Status))
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
        StructaDocParseRunId = model.StructaDocParseRunId,
        MarkdownContent = model.MarkdownContent,
        ContentList = model.ContentList,
        ContentListV2 = model.ContentListV2,
        ModelJson = model.ModelJson,
        LayoutJson = model.LayoutJson,
        ZipPath = model.ZipPath,
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
        StructaDocParseRunId = entity.StructaDocParseRunId,
        MarkdownContent = entity.MarkdownContent,
        ContentList = entity.ContentList,
        ContentListV2 = entity.ContentListV2,
        ModelJson = entity.ModelJson,
        LayoutJson = entity.LayoutJson,
        ZipPath = entity.ZipPath,
        ErrorMessage = entity.ErrorMessage,
        ParsedAt = entity.ParsedAt,
    };
}
