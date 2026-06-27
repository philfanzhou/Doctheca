using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.DocLibrary.Database.Entities;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;

namespace Ruoyu.Study.DocLibrary.Database.Repositories;

public class DocumentRepository : IDocumentRepository
{
    private readonly DocLibraryDbContext _dbContext;

    public DocumentRepository(DocLibraryDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(DocumentModel model)
    {
        await _dbContext.Documents.AddAsync(MapToEntity(model));
    }

    public async Task<DocumentModel?> GetByIdAsync(Guid id)
    {
        var entity = await _dbContext.Documents.FindAsync(id);
        return entity != null ? MapToModel(entity) : null;
    }

    public async Task<DocumentModel?> GetByTitleAsync(string title)
    {
        var entity = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Title == title);
        return entity != null ? MapToModel(entity) : null;
    }

    public async Task<DocumentModel?> GetByFileHashAsync(string fileHash)
    {
        var entity = await _dbContext.Documents.FirstOrDefaultAsync(d => d.FileHash == fileHash);
        return entity != null ? MapToModel(entity) : null;
    }

    public async Task<DocumentModel?> GetByFileHashAndStatusAsync(string fileHash, string status)
    {
        var entity = await _dbContext.Documents.FirstOrDefaultAsync(d => d.FileHash == fileHash && d.Status == status);
        return entity != null ? MapToModel(entity) : null;
    }

    public async Task<bool> UpdateAsync(DocumentModel model)
    {
        var entity = await _dbContext.Documents.FindAsync(model.Id);
        if (entity == null) return false;

        entity.Title = model.Title;
        entity.SourceType = model.SourceType;
        entity.FileHash = model.FileHash;
        entity.FilePath = model.FilePath;
        entity.FileSize = model.FileSize;
        entity.Language = model.Language;
        entity.Grade = model.Grade;
        entity.Subject = model.Subject;
        entity.Year = model.Year;
        entity.Tags = model.Tags;
        entity.Status = model.Status;
        entity.UpdatedAt = model.UpdatedAt;

        _dbContext.Documents.Update(entity);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var entity = await _dbContext.Documents.FindAsync(id);
        if (entity == null) return false;

        _dbContext.Documents.Remove(entity);
        return true;
    }

    public async Task<(List<DocumentModel> Items, int TotalCount)> GetListAsync(
        int page, int size, string? status = null, string? subject = null, string? grade = null, string? keyword = null, string? year = null)
    {
        var query = _dbContext.Documents.AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(d => d.Status == status);
        if (!string.IsNullOrWhiteSpace(subject))
            query = query.Where(d => d.Subject == subject);
        if (!string.IsNullOrWhiteSpace(grade))
            query = query.Where(d => d.Grade == grade);
        if (!string.IsNullOrWhiteSpace(keyword))
            query = query.Where(d => d.Title.Contains(keyword));
        if (!string.IsNullOrWhiteSpace(year))
            query = query.Where(d => d.Year == year);

        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(d => d.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();

        return (items.Select(MapToModel).ToList(), totalCount);
    }

    public async Task<List<(Guid Id, string Title, string FilePath, string Status)>> GetAllDocumentsWithFilePathAsync()
    {
        return await _dbContext.Documents
            .Where(d => d.FilePath != null && d.FilePath != "")
            .Select(d => new ValueTuple<Guid, string, string, string>(d.Id, d.Title, d.FilePath, d.Status))
            .ToListAsync();
    }

    private static DocumentEntity MapToEntity(DocumentModel model) => new()
    {
        Id = model.Id,
        Title = model.Title,
        SourceType = model.SourceType,
        FileHash = model.FileHash,
        FilePath = model.FilePath,
        FileSize = model.FileSize,
        Language = model.Language,
        Grade = model.Grade,
        Subject = model.Subject,
        Year = model.Year,
        Tags = model.Tags,
        Status = model.Status,
        CreatedBy = model.CreatedBy,
        CreatedAt = model.CreatedAt,
        UpdatedAt = model.UpdatedAt
    };

    private static DocumentModel MapToModel(DocumentEntity entity) => new()
    {
        Id = entity.Id,
        Title = entity.Title,
        SourceType = entity.SourceType,
        FileHash = entity.FileHash,
        FilePath = entity.FilePath,
        FileSize = entity.FileSize,
        Language = entity.Language,
        Grade = entity.Grade,
        Subject = entity.Subject,
        Year = entity.Year,
        Tags = entity.Tags,
        Status = entity.Status,
        CreatedBy = entity.CreatedBy,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt
    };
}
