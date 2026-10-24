using System.Data;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Doctheca.Database.Repositories;

public sealed partial class InternalStructureRepository(DbContextOptions<DocthecaDbContext> options) : IInternalStructureRepository, IInternalContentRepository
{
    // Anonymous reads use the same safe logging boundary as the internal catalogs.
    // Keep provider messages out of EF diagnostics without changing administrative logging.
    private DocthecaDbContext CreateContext() => new(new DbContextOptionsBuilder<DocthecaDbContext>(options)
        .UseLoggerFactory(NullLoggerFactory.Instance).EnableSensitiveDataLogging(false).EnableDetailedErrors(false).Options);

    private static Task<Guid?> CurrentTreeAsync(DocthecaDbContext context, Guid documentFileId, CancellationToken cancellationToken) =>
        context.DocumentFiles.AsNoTracking().Where(file => file.Id == documentFileId)
            .Select(file => file.CurrentStructureTreeId).SingleOrDefaultAsync(cancellationToken);

    public async Task<InternalStructureRead> ReadTreeAsync(Guid documentFileId, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var document = await context.DocumentFiles.AsNoTracking().Where(file => file.Id == documentFileId)
            .Select(file => new { file.StructureDefinitionId }).SingleOrDefaultAsync(cancellationToken);
        if (document is null) return new(InternalStructureReadStatus.DocumentNotFound);
        if (await CurrentTreeAsync(context, documentFileId, cancellationToken) is not { } treeId)
            return new(InternalStructureReadStatus.StructureNotFound);

        var levels = await context.StructureLevels.AsNoTracking().Where(level => level.DefinitionId == document.StructureDefinitionId)
            .Select(level => new { level.Id, level.Name }).ToDictionaryAsync(level => level.Id, level => level.Name, cancellationToken);
        var nodes = await context.DocumentStructureNodes.AsNoTracking().Where(node => node.TreeId == treeId)
            .Select(node => new NodeRow(node.Id, node.LevelId, node.ParentId, node.Name, node.SortOrder,
                node.StartSequence, node.EndSequence,
                context.DocumentParseBlocks.Where(block => block.ParseId == node.ParseId && block.Sequence >= node.StartSequence && block.Sequence <= node.EndSequence)
                    .Min(block => (int?)(block.PageId + 1)),
                context.DocumentParseBlocks.Where(block => block.ParseId == node.ParseId && block.Sequence >= node.StartSequence && block.Sequence <= node.EndSequence)
                    .Max(block => (int?)(block.PageId + 1)),
                context.DocumentStructureAudio.Count(audio => audio.TreeId == node.TreeId && audio.DocumentFileId == node.DocumentFileId && audio.NodeId == node.Id &&
                    node.Level!.DefinitionId == document.StructureDefinitionId)))
            .ToListAsync(cancellationToken);

        if (nodes.Count == 0) return new(InternalStructureReadStatus.StructureNotFound);
        var children = nodes.ToLookup(node => node.ParentId);
        InternalStructureNode Project(NodeRow node, string? parentPath)
        {
            var path = parentPath is null ? node.Name : parentPath + " > " + node.Name;
            return new(node.Id, node.LevelId, levels[node.LevelId], node.Name, path, node.StartSequence, node.EndSequence,
                node.StartPageNumber, node.EndPageNumber, Ordered(node.Id).Select(child => Project(child, path)).ToList(), node.AudioCount);
        }
        IEnumerable<NodeRow> Ordered(Guid? parent) => children[parent].OrderBy(node => node.SortOrder)
            .ThenBy(node => node.StartSequence).ThenBy(node => node.Id);
        var bindingIds = await context.DocumentStructureNodes.Where(node => node.TreeId == treeId)
            .Select(node => node.ParseId).Distinct().ToListAsync(cancellationToken);
        var parseId = bindingIds.Count == 1 ? bindingIds[0] : null;
        var tree = new InternalStructureTree(documentFileId, parseId, Ordered(null).Select(node => Project(node, null)).ToList());
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        return new(InternalStructureReadStatus.Found, tree);
    }

    public async Task<InternalCurrentStructureNode?> ReadCurrentNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var node = await CurrentNodeAsync(context, nodeId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        return node;
    }

    private static async Task<InternalCurrentStructureNode?> CurrentNodeAsync(DocthecaDbContext context, Guid nodeId, CancellationToken cancellationToken)
    {
        var node = await (from item in context.DocumentStructureNodes.AsNoTracking()
            join file in context.DocumentFiles.AsNoTracking() on item.DocumentFileId equals file.Id
            where item.Id == nodeId && file.CurrentStructureTreeId == item.TreeId && item.Level!.DefinitionId == file.StructureDefinitionId
            select new InternalCurrentStructureNode(file.Id, item.ParseId, item.Id, item.StartSequence, item.EndSequence, item.TreeId))
            .SingleOrDefaultAsync(cancellationToken);
        return node;
    }

    private sealed record NodeRow(Guid Id, Guid LevelId, Guid? ParentId, string Name, int SortOrder,
        int? StartSequence, int? EndSequence, int? StartPageNumber, int? EndPageNumber, int AudioCount);
}
