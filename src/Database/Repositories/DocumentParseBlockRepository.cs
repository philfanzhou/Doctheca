using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.DocLibrary.Database.Entities;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;

namespace Ruoyu.Study.DocLibrary.Database.Repositories;

public class DocumentParseBlockRepository : IDocumentParseBlockRepository
{
    private readonly DocLibraryDbContext _context;

    public DocumentParseBlockRepository(DocLibraryDbContext context)
    {
        _context = context;
    }

    public async Task AddBlocksAsync(Guid parseId, IEnumerable<DocumentParseBlockModel> blocks)
    {
        var blockList = blocks.ToList();
        if (blockList.Count == 0) return;

        // Overwrite strategy: delete existing blocks for this parse first
        var existing = await _context.DocumentParseBlocks
            .Where(b => b.ParseId == parseId)
            .ToListAsync();
        if (existing.Count > 0)
        {
            _context.DocumentParseBlocks.RemoveRange(existing);
        }

        var entities = blockList.Select(MapToEntity).ToList();
        _context.DocumentParseBlocks.AddRange(entities);
        await _context.SaveChangesAsync();
    }

    public async Task<List<DocumentParseBlockModel>> GetByParseIdAsync(Guid parseId)
    {
        return await _context.DocumentParseBlocks
            .Where(b => b.ParseId == parseId)
            .OrderBy(b => b.PageId)
            .ThenBy(b => b.SortIndex)
            .Select(b => MapToModel(b))
            .ToListAsync();
    }

    public async Task<List<DocumentParseBlockModel>> GetByParseAndPageAsync(Guid parseId, int pageId)
    {
        return await _context.DocumentParseBlocks
            .Where(b => b.ParseId == parseId && b.PageId == pageId)
            .OrderBy(b => b.SortIndex)
            .Select(b => MapToModel(b))
            .ToListAsync();
    }

    private static DocumentParseBlockEntity MapToEntity(DocumentParseBlockModel model) => new()
    {
        Id = model.Id,
        ParseId = model.ParseId,
        PageId = model.PageId,
        SortIndex = model.SortIndex,
        BlockType = model.BlockType,
        TextContent = model.TextContent,
        ImageId = model.ImageId,
        BlockData = model.BlockData,
        CreatedAt = model.CreatedAt,
        // [Gen-2] minerU structured fields
        SubType = model.SubType,
        TextLevel = model.TextLevel,
        TextFormat = model.TextFormat,
        BboxX0 = model.BboxX0,
        BboxY0 = model.BboxY0,
        BboxX1 = model.BboxX1,
        BboxY1 = model.BboxY1,
        MineruScore = model.MineruScore,
        Caption = model.Caption,
    };

    private static DocumentParseBlockModel MapToModel(DocumentParseBlockEntity entity) => new()
    {
        Id = entity.Id,
        ParseId = entity.ParseId,
        PageId = entity.PageId,
        SortIndex = entity.SortIndex,
        BlockType = entity.BlockType,
        TextContent = entity.TextContent,
        ImageId = entity.ImageId,
        BlockData = entity.BlockData,
        CreatedAt = entity.CreatedAt,
        // [Gen-2] minerU structured fields
        SubType = entity.SubType,
        TextLevel = entity.TextLevel,
        TextFormat = entity.TextFormat,
        BboxX0 = entity.BboxX0,
        BboxY0 = entity.BboxY0,
        BboxX1 = entity.BboxX1,
        BboxY1 = entity.BboxY1,
        MineruScore = entity.MineruScore,
        Caption = entity.Caption,
    };
}
