using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Database;
using Ruoyu.Study.DocLibrary.Database.Entities;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Services;

namespace Ruoyu.Study.DocLibrary.Service;

/// <summary>
/// Implementation of QuestionBank pull-mode integration service.
/// Reads MinerU parsed data (document_parses / document_parse_blocks / document_parse_images)
/// and tracks QuestionBank import status (document_parse_imports).
/// </summary>
public class QuestionBankImportService : IQuestionBankImportService
{
    private const int MaxListPageSize = 100;
    private const int DefaultListPageSize = 20;
    private const int MaxBlocksPageSize = 200;
    private const int DefaultBlocksPageSize = 50;
    private const int PresignedUrlExpirySeconds = 3600;

    private readonly DocLibraryDbContext _dbContext;
    private readonly IDocumentParseImportRepository _importRepository;
    private readonly IOssService _ossService;
    private readonly ILogger<QuestionBankImportService> _logger;

    public QuestionBankImportService(
        DocLibraryDbContext dbContext,
        IDocumentParseImportRepository importRepository,
        IOssService ossService,
        ILogger<QuestionBankImportService> logger)
    {
        _dbContext = dbContext;
        _importRepository = importRepository;
        _ossService = ossService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<(List<ImportableParseItem> Items, int TotalCount)> GetImportableListAsync(
        int page,
        int pageSize,
        string? search = null,
        bool includeImported = false)
    {
        if (page <= 0) page = 1;
        if (pageSize <= 0) pageSize = DefaultListPageSize;
        if (pageSize > MaxListPageSize) pageSize = MaxListPageSize;

        var query = from p in _dbContext.DocumentParses
                    join f in _dbContext.DocumentFiles on p.DocumentFileId equals f.Id
                    join i in _dbContext.DocumentParseImports on p.Id equals i.ParseId into imports
                    from i in imports.DefaultIfEmpty()
                    where p.Status == DocumentParseStatus.Parsed
                    select new { p, f, i };

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(x => x.f.FileName.Contains(search));
        }

        if (!includeImported)
        {
            // Exclude parses already marked as 'imported'; 'failed' and NULL are still listed
            query = query.Where(x => x.i == null || x.i.Status != ParseImportStatus.Imported);
        }

        var totalCount = await query.CountAsync();

        var rows = await query
            .OrderByDescending(x => x.p.ParsedAt)
            .ThenByDescending(x => x.p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ImportableParseItem(
                x.p.Id,
                x.f.Id,
                x.f.FileName,
                x.p.ModelVersion,
                x.p.ParsedAt,
                x.i != null ? x.i.Status : null,
                x.i != null ? x.i.CreatedAt : (DateTimeOffset?)null))
            .ToListAsync();

        return (rows, totalCount);
    }

    /// <inheritdoc />
    public async Task<(List<ParseBlockItem> Items, int TotalCount)> GetBlocksAsync(
        Guid parseId,
        int? pageId,
        string? blockType,
        int page,
        int pageSize)
    {
        if (page <= 0) page = 1;
        if (pageSize <= 0) pageSize = DefaultBlocksPageSize;
        if (pageSize > MaxBlocksPageSize) pageSize = MaxBlocksPageSize;

        // Verify parse exists and is in parsed status
        var parse = await _dbContext.DocumentParses.FindAsync(parseId)
            ?? throw new KeyNotFoundException($"Document parse not found: {parseId}");

        if (parse.Status != DocumentParseStatus.Parsed)
            throw new InvalidOperationException($"Parse is not in parsed status (current: {parse.Status})");

        var query = _dbContext.DocumentParseBlocks
            .Where(b => b.ParseId == parseId);

        if (pageId.HasValue)
        {
            query = query.Where(b => b.PageId == pageId.Value);
        }

        if (!string.IsNullOrWhiteSpace(blockType))
        {
            query = query.Where(b => b.BlockType == blockType);
        }

        var totalCount = await query.CountAsync();

        var blocks = await query
            .OrderBy(b => b.PageId)
            .ThenBy(b => b.SortIndex)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(b => b.Image) // N+1 prevention: preload image
            .ToListAsync();

        // Parallelize presigned URL generation to avoid sequential OSS calls per block
        var imageBlocks = blocks.Where(b => b.ImageId.HasValue && b.Image != null).ToList();
        var urlTasks = imageBlocks.Select(async b =>
        {
            try
            {
                var url = await _ossService.GetPresignedUrlAsync(b.Image!.ImagePath, PresignedUrlExpirySeconds);
                return (BlockId: b.Id, Url: (string?)url);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate presigned URL for image {ImageId} at {ImagePath}",
                    b.Image!.Id, b.Image.ImagePath);
                return (BlockId: b.Id, Url: (string?)null);
            }
        }).ToList();
        var urlMap = (await Task.WhenAll(urlTasks)).ToDictionary(x => x.BlockId, x => x.Url);

        var items = new List<ParseBlockItem>(blocks.Count);
        foreach (var b in blocks)
        {
            string? imageUrl = null;
            string? imageName = null;
            string? imagePath = null;

            if (b.ImageId.HasValue && b.Image != null)
            {
                imageName = b.Image.ImageName;
                imagePath = b.Image.ImagePath;
                urlMap.TryGetValue(b.Id, out imageUrl);
            }
            else if (b.ImageId.HasValue && b.Image == null)
            {
                _logger.LogWarning("Block {BlockId} has image_id {ImageId} but image record not found (data inconsistency)",
                    b.Id, b.ImageId);
            }

            items.Add(new ParseBlockItem(
                b.Id,
                b.ParseId,
                b.PageId,
                b.SortIndex,
                b.BlockType,
                b.TextContent,
                imageName,
                imagePath,
                imageUrl,
                ParseBlockData(b.BlockData)));
        }

        return (items, totalCount);
    }

    /// <inheritdoc />
    public async Task<ParseImageBlob> GetImageBlobAsync(Guid imageId)
    {
        var image = await _dbContext.DocumentParseImages.FindAsync(imageId)
            ?? throw new KeyNotFoundException($"Image not found: {imageId}");

        Stream stream;
        try
        {
            stream = await _ossService.DownloadAsync(image.ImagePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download image {ImageId} from OSS path {ImagePath}",
                imageId, image.ImagePath);
            throw;
        }

        return new ParseImageBlob(image.ImageName, image.ContentType, stream);
    }

    /// <inheritdoc />
    public async Task<ImportStatusResult> UpsertImportStatusAsync(
        Guid parseId,
        Guid importedBy,
        string status,
        string? note,
        string? importedQuestionIds)
    {
        if (status != ParseImportStatus.Imported && status != ParseImportStatus.Failed)
            throw new ArgumentException($"Status must be '{ParseImportStatus.Imported}' or '{ParseImportStatus.Failed}'");

        // Verify parse exists
        var parse = await _dbContext.DocumentParses.FindAsync(parseId)
            ?? throw new KeyNotFoundException($"Document parse not found: {parseId}");

        var existing = await _importRepository.GetByParseIdAsync(parseId);

        if (existing != null)
        {
            // Prevent re-importing an already-imported parse (allow imported->failed and failed->imported)
            if (existing.Status == ParseImportStatus.Imported && status == ParseImportStatus.Imported)
            {
                throw new InvalidOperationException("Parse is already marked as imported");
            }

            existing.Status = status;
            existing.Note = note;
            existing.ImportedQuestionIds = importedQuestionIds;
            await _importRepository.UpdateAsync(existing);

            return new ImportStatusResult(
                parseId,
                status,
                existing.CreatedAt,
                DateTimeOffset.UtcNow);
        }

        var model = new DocumentParseImportModel
        {
            ParseId = parseId,
            ImportedBy = importedBy,
            Status = status,
            Note = note,
            ImportedQuestionIds = importedQuestionIds,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var created = await _importRepository.AddAsync(model);

        return new ImportStatusResult(
            parseId,
            status,
            created.CreatedAt,
            null);
    }

    /// <summary>
    /// Parse block_data JSON string into an object. Returns null on failure.
    /// </summary>
    private object? ParseBlockData(string? blockData)
    {
        if (string.IsNullOrWhiteSpace(blockData))
            return null;

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(blockData);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse block_data as JSON, returning null. Data: {Data}", blockData);
            return null;
        }
    }
}
