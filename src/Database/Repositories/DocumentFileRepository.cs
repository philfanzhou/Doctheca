using Microsoft.EntityFrameworkCore;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;

namespace Doctheca.Database.Repositories;

public class DocumentFileRepository : IDocumentFileRepository
{
    private readonly DocthecaDbContext _context;

    public DocumentFileRepository(DocthecaDbContext context)
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
        await using var transaction = await _context.Database.BeginTransactionAsync();
        var entity = await StructureOwnership.LockDocumentAsync(_context, model.Id);
        if (entity == null) return false;

        entity.FileName = model.FileName;
        entity.FilePath = model.FilePath;
        entity.ContentType = model.ContentType;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await _context.SaveChangesAsync();
        await transaction.CommitAsync();
        return true;
    }

    public async Task<bool> UpdateMetadataAsync(Guid id, string? subject, string? grade, string? year)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync();
        var entity = await StructureOwnership.LockDocumentAsync(_context, id);
        if (entity == null) return false;

        // Only update fields that are non-null (null = no change)
        if (subject != null) entity.Subject = subject;
        if (grade != null) entity.Grade = grade;
        if (year != null) entity.Year = year;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await _context.SaveChangesAsync();
        await transaction.CommitAsync();
        return true;
    }

    public async Task<bool> AttachStructaDocDocumentAsync(Guid id, Guid structaDocDocumentId)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync();
        var entity = await StructureOwnership.LockDocumentAsync(_context, id);
        if (entity == null) return false;

        entity.StructaDocDocumentId = structaDocDocumentId;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await _context.SaveChangesAsync();
        await transaction.CommitAsync();
        return true;
    }

    public async Task<bool> SetStructureDefinitionAsync(Guid id, Guid? structureDefinitionId)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync();
        var entity = await StructureOwnership.LockDocumentAsync(_context, id);
        if (entity == null) return false;

        if (entity.StructureDefinitionId != structureDefinitionId && await _context.DocumentStructureNodes.AnyAsync(node => node.DocumentFileId == id)) return false;
        entity.StructureDefinitionId = structureDefinitionId;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await _context.SaveChangesAsync();
        await transaction.CommitAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id) => await DeleteWithAudioAsync(id) is not null;

    public async Task<IReadOnlyList<string>?> DeleteWithAudioAsync(Guid id)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync();
        var file = await StructureOwnership.LockDocumentAsync(_context, id);
        if (file is null) return null;
        var keys = await _context.DocumentStructureAudio.Where(audio => audio.DocumentFileId == id)
            .Select(audio => audio.ObjectKey).ToListAsync();
        file.CurrentStructureTreeId = null;
        await _context.SaveChangesAsync();
        // Clear optional parse references before the document's parses and trees cascade.
        await _context.DocumentStructureNodes.Where(node => node.DocumentFileId == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(node => node.ParseId, (Guid?)null).SetProperty(node => node.StartSequence, (int?)null)
            .SetProperty(node => node.EndSequence, (int?)null));
        _context.DocumentFiles.Remove(file);
        await _context.SaveChangesAsync();
        await transaction.CommitAsync();
        _context.ChangeTracker.Clear();
        return keys;
    }

    private static DocumentFileEntity MapToEntity(DocumentFileModel model) => new()
    {
        Id = model.Id,
        FileName = model.FileName,
        FilePath = model.FilePath,
        StructaDocDocumentId = model.StructaDocDocumentId,
        ContentType = model.ContentType,
        CreatedBy = model.CreatedBy,
        Subject = model.Subject,
        Grade = model.Grade,
        Year = model.Year,
        StructureDefinitionId = model.StructureDefinitionId,
        CreatedAt = model.CreatedAt,
        UpdatedAt = model.UpdatedAt,
    };

    private static DocumentFileModel MapToModel(DocumentFileEntity entity) => new()
    {
        Id = entity.Id,
        FileName = entity.FileName,
        FilePath = entity.FilePath,
        StructaDocDocumentId = entity.StructaDocDocumentId,
        ContentType = entity.ContentType,
        CreatedBy = entity.CreatedBy,
        Subject = entity.Subject,
        Grade = entity.Grade,
        Year = entity.Year,
        StructureDefinitionId = entity.StructureDefinitionId,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
    };
}
