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
        await using var transaction = await _context.Database.BeginTransactionAsync();
        var file = await StructureOwnership.LockDocumentAsync(_context, model.DocumentFileId)
            ?? throw new KeyNotFoundException("Document file not found.");
        model.ExpectedBindingGeneration = file.StructureBindingGeneration;
        var entity = MapToEntity(model);
        _context.DocumentParses.Add(entity);
        await _context.SaveChangesAsync();
        model.Id = entity.Id;
        await transaction.CommitAsync();
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
        await using var transaction = await _context.Database.BeginTransactionAsync();
        _ = await StructureOwnership.LockParseOwnerAsync(_context, model.Id)
            ?? throw new KeyNotFoundException("Document parse not found.");
        var entity = await _context.DocumentParses.SingleAsync(parse => parse.Id == model.Id);
        await _context.Entry(entity).ReloadAsync();
        if (entity.Status == DocumentParseStatus.Parsed) return MapToModel(entity);
        // All successful content writes use CompleteAsync; status-only updates cannot erase bindings.
        if (model.Status == DocumentParseStatus.Parsed)
            throw new InvalidOperationException("Successful parse results must be committed atomically.");

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
        await transaction.CommitAsync();
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
        await using var transaction = await _context.Database.BeginTransactionAsync();
        var file = await StructureOwnership.LockParseOwnerAsync(_context, id);
        if (file is null) return;
        await _context.DocumentStructureNodes.Where(node => node.ParseId == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(node => node.ParseId, (Guid?)null).SetProperty(node => node.StartSequence, (int?)null)
            .SetProperty(node => node.EndSequence, (int?)null));
        file.StructureBindingGeneration++;
        await _context.DocumentParses.Where(parse => parse.Id == id).ExecuteDeleteAsync();
        await _context.SaveChangesAsync();
        await transaction.CommitAsync();
        _context.ChangeTracker.Clear();
    }

    public async Task<string> CompleteAsync(Guid parseId, Guid runId, string markdown,
        IReadOnlyCollection<DocumentParsePageModel> pages, IReadOnlyList<DocumentParseImageModel> images,
        IReadOnlyList<DocumentParseBlockModel> blocks, CancellationToken ct = default)
    {
        if (pages.Any(page => page.ParseId != parseId || page.PageNumber <= 0) ||
            pages.Select(page => page.PageNumber).Distinct().Count() != pages.Count ||
            images.Any(image => image.ParseId != parseId) || blocks.Any(block => block.ParseId != parseId))
            throw new ArgumentException("Parse results must have one owner and unique positive pages.");
        await using var transaction = await _context.Database.BeginTransactionAsync(ct);
        try
        {
            var file = await StructureOwnership.LockParseOwnerAsync(_context, parseId, ct)
                ?? throw new KeyNotFoundException("Document parse not found.");
            var parse = await _context.DocumentParses.SingleAsync(item => item.Id == parseId, ct);
            await _context.Entry(parse).ReloadAsync(ct);
            if (parse.Status == DocumentParseStatus.Parsed) return parse.MarkdownContent ?? string.Empty;
            if (parse.Status == DocumentParseStatus.Failed) throw new InvalidOperationException("A failed parse cannot complete.");
            await _context.DocumentParsePages.Where(page => page.ParseId == parseId).ExecuteDeleteAsync(ct);
            await _context.DocumentParseBlocks.Where(block => block.ParseId == parseId).ExecuteDeleteAsync(ct);
            await _context.DocumentParseImages.Where(image => image.ParseId == parseId).ExecuteDeleteAsync(ct);
            _context.DocumentParsePages.AddRange(pages.Select(page => new DocumentParsePageEntity {
                ParseId = parseId, PageNumber = page.PageNumber, Label = page.Label, LabelSource = page.LabelSource }));
            _context.DocumentParseImages.AddRange(images.Select(DocumentParseImageRepository.MapToEntity));
            _context.DocumentParseBlocks.AddRange(blocks.Select(DocumentParseBlockRepository.MapToEntity));
            parse.Status = DocumentParseStatus.Parsed;
            parse.MarkdownContent = markdown;
            parse.StructaDocParseRunId = runId;
            parse.ParsedAt = DateTimeOffset.UtcNow;
            parse.ErrorMessage = null;
            if (parse.ExpectedBindingGeneration == file.StructureBindingGeneration)
            {
                await _context.DocumentStructureNodes.Where(node => node.TreeId == file.CurrentStructureTreeId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(node => node.ParseId, (Guid?)null)
                        .SetProperty(node => node.StartSequence, (int?)null).SetProperty(node => node.EndSequence, (int?)null), ct);
                file.StructureBindingGeneration++;
            }
            await _context.SaveChangesAsync(ct);
            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync(ct);
            return markdown;
        }
        finally { _context.ChangeTracker.Clear(); }
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
        ExpectedBindingGeneration = model.ExpectedBindingGeneration,
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
        ExpectedBindingGeneration = entity.ExpectedBindingGeneration,
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
