using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.DocLibrary.Database.Entities;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;

namespace Ruoyu.Study.DocLibrary.Database.Repositories;

public class DocumentParseImportRepository : IDocumentParseImportRepository
{
    private readonly DocLibraryDbContext _context;

    public DocumentParseImportRepository(DocLibraryDbContext context)
    {
        _context = context;
    }

    public async Task<DocumentParseImportModel?> GetByParseIdAsync(Guid parseId)
    {
        var entity = await _context.DocumentParseImports
            .FirstOrDefaultAsync(e => e.ParseId == parseId);
        return entity != null ? MapToModel(entity) : null;
    }

    public async Task<DocumentParseImportModel> AddAsync(DocumentParseImportModel model)
    {
        var entity = MapToEntity(model);
        _context.DocumentParseImports.Add(entity);
        await _context.SaveChangesAsync();
        model.Id = entity.Id;
        model.CreatedAt = entity.CreatedAt;
        return model;
    }

    public async Task UpdateAsync(DocumentParseImportModel model)
    {
        var entity = await _context.DocumentParseImports.FindAsync(model.Id)
            ?? throw new KeyNotFoundException($"Document parse import not found: {model.Id}");

        entity.Status = model.Status;
        entity.Note = model.Note;
        entity.ImportedQuestionIds = model.ImportedQuestionIds;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await _context.SaveChangesAsync();
    }

    private static DocumentParseImportEntity MapToEntity(DocumentParseImportModel model) => new()
    {
        Id = model.Id,
        ParseId = model.ParseId,
        ImportedBy = model.ImportedBy,
        Status = model.Status,
        Note = model.Note,
        ImportedQuestionIds = model.ImportedQuestionIds,
        CreatedAt = model.CreatedAt,
        UpdatedAt = model.UpdatedAt,
    };

    private static DocumentParseImportModel MapToModel(DocumentParseImportEntity entity) => new()
    {
        Id = entity.Id,
        ParseId = entity.ParseId,
        ImportedBy = entity.ImportedBy,
        Status = entity.Status,
        Note = entity.Note,
        ImportedQuestionIds = entity.ImportedQuestionIds,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
    };
}
