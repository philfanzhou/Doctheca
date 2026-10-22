using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Doctheca.Database.Repositories;

public sealed partial class InternalStructureRepository
{
    private static IQueryable<DocumentParseBlockEntity> InRange(DocthecaDbContext context, InternalCurrentStructureNode node) =>
        context.DocumentParseBlocks.AsNoTracking().Where(block => block.ParseId == node.ParseId &&
            block.Sequence >= node.StartSequence && block.Sequence <= node.EndSequence);

    private static async Task<string> RevisionAsync(DocthecaDbContext context, InternalCurrentStructureNode node, CancellationToken cancellationToken)
    {
        // Canonical property and row order; never expose the hash input or use coarse timestamps alone.
        var tree = await context.DocumentStructureNodes.AsNoTracking().Where(item => item.ParseId == node.ParseId)
            .OrderBy(item => item.Id).Select(item => new { item.Id, item.LevelId, item.ParentId, item.Name,
                item.StartSequence, item.EndSequence, item.SortOrder, item.CreatedAt, item.UpdatedAt,
                item.Level!.DefinitionId, LevelName = item.Level.Name }).ToListAsync(cancellationToken);
        var blocks = await InRange(context, node).OrderBy(block => block.Sequence)
            .Select(block => new { block.Id, block.Sequence, block.ImageId }).ToListAsync(cancellationToken);
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { node.ParseId, tree, blocks })));
    }

    public async Task<InternalContentRead> ReadContentAsync(Guid nodeId, InternalContentQuery query, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var node = await CurrentNodeAsync(context, nodeId, cancellationToken);
        if (node is null) return new(InternalContentStatus.NotFound);
        var revision = await RevisionAsync(context, node, cancellationToken);
        if (query.Revision is not null && !string.Equals(query.Revision, revision, StringComparison.OrdinalIgnoreCase))
            return new(InternalContentStatus.Changed);
        if (query.AfterSequence is { } after && (after < node.StartSequence || after > node.EndSequence))
            return new(InternalContentStatus.InvalidCursor);
        var rows = await InRange(context, node).Where(block => query.AfterSequence == null || block.Sequence > query.AfterSequence)
            .OrderBy(block => block.Sequence).Take(query.Limit + 1)
            .Select(block => new InternalContentItem(block.Id, block.Sequence!.Value, block.BlockType, block.SubType,
                block.TextContent, block.TextFormat == "" ? null : block.TextFormat, block.PageId + 1,
                block.ImageId, null)).ToListAsync(cancellationToken);
        var hasMore = rows.Count > query.Limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var items = rows.Select(block => block with { ImageUrl = block.ImageId is { } imageId
            ? $"/internal/v1/structure-nodes/{nodeId:D}/images/{imageId:D}/content?revision={revision}" : null }).ToList();
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        return new(InternalContentStatus.Found, new(node.ParseId, nodeId, revision, items, hasMore ? items[^1].Sequence : null, hasMore));
    }

    public async Task<InternalImageRead> ReadImageAsync(Guid nodeId, Guid imageId, string revision, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var node = await CurrentNodeAsync(context, nodeId, cancellationToken);
        if (node is null) return new(InternalContentStatus.NotFound);
        if (!string.Equals(revision, await RevisionAsync(context, node, cancellationToken), StringComparison.OrdinalIgnoreCase))
            return new(InternalContentStatus.Changed);
        var blocks = InRange(context, node);
        var image = await context.DocumentParseImages.AsNoTracking().Where(item => item.Id == imageId && item.ParseId == node.ParseId &&
                blocks.Any(block => block.ImageId == item.Id))
            .Select(item => new DocumentParseImageModel { Id = item.Id, ParseId = item.ParseId, ImagePath = item.ImagePath, ContentType = item.ContentType })
            .SingleOrDefaultAsync(cancellationToken);
        if (image is null) return new(InternalContentStatus.NotFound);
        var parse = await context.DocumentParses.AsNoTracking().Where(item => item.Id == node.ParseId)
            .Select(item => new DocumentParseModel { Id = item.Id, StructaDocParseRunId = item.StructaDocParseRunId }).SingleAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        return new(InternalContentStatus.Found, parse, image);
    }
}
