using System.Data;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Doctheca.Database.Repositories;

public sealed class InternalStructureRepository(DbContextOptions<DocthecaDbContext> options) : IInternalStructureRepository
{
    // Anonymous reads use the same safe logging boundary as the internal catalogs.
    // Keep provider messages out of EF diagnostics without changing administrative logging.
    private DocthecaDbContext CreateContext() => new(new DbContextOptionsBuilder<DocthecaDbContext>(options)
        .UseLoggerFactory(NullLoggerFactory.Instance).EnableSensitiveDataLogging(false).EnableDetailedErrors(false).Options);

    private static Task<Guid?> CurrentParseAsync(DocthecaDbContext context, Guid documentFileId, CancellationToken cancellationToken) =>
        context.DocumentParses.AsNoTracking()
            .Where(parse => parse.DocumentFileId == documentFileId && parse.Status == DocumentParseStatus.Parsed &&
                context.DocumentStructureNodes.Any(node => node.ParseId == parse.Id))
            // PostgreSQL sorts nulls first for DESC by default; explicitly put them last.
            .OrderByDescending(parse => parse.ParsedAt.HasValue).ThenByDescending(parse => parse.ParsedAt)
            .ThenByDescending(parse => parse.Id).Select(parse => (Guid?)parse.Id).FirstOrDefaultAsync(cancellationToken);

    public async Task<InternalStructureRead> ReadTreeAsync(Guid documentFileId, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var document = await context.DocumentFiles.AsNoTracking().Where(file => file.Id == documentFileId)
            .Select(file => new { file.StructureDefinitionId }).SingleOrDefaultAsync(cancellationToken);
        if (document is null) return new(InternalStructureReadStatus.DocumentNotFound);
        if (await CurrentParseAsync(context, documentFileId, cancellationToken) is not { } parseId)
            return new(InternalStructureReadStatus.StructureNotFound);

        var levels = await context.StructureLevels.AsNoTracking().Where(level => level.DefinitionId == document.StructureDefinitionId)
            .Select(level => new { level.Id, level.Name }).ToDictionaryAsync(level => level.Id, level => level.Name, cancellationToken);
        var nodes = await context.DocumentStructureNodes.AsNoTracking().Where(node => node.ParseId == parseId)
            .Select(node => new NodeRow(node.Id, node.LevelId, node.ParentId, node.Name, node.SortOrder,
                node.StartSequence, node.EndSequence,
                context.DocumentParseBlocks.Where(block => block.ParseId == parseId && block.Sequence >= node.StartSequence && block.Sequence <= node.EndSequence)
                    .Min(block => (int?)(block.PageId + 1)),
                context.DocumentParseBlocks.Where(block => block.ParseId == parseId && block.Sequence >= node.StartSequence && block.Sequence <= node.EndSequence)
                    .Max(block => (int?)(block.PageId + 1))))
            .ToListAsync(cancellationToken);

        var children = nodes.ToLookup(node => node.ParentId);
        InternalStructureNode Project(NodeRow node, string? parentPath)
        {
            var path = parentPath is null ? node.Name : parentPath + " > " + node.Name;
            return new(node.Id, node.LevelId, levels[node.LevelId], node.Name, path, node.StartSequence, node.EndSequence,
                node.StartPageNumber, node.EndPageNumber, Ordered(node.Id).Select(child => Project(child, path)).ToList());
        }
        IEnumerable<NodeRow> Ordered(Guid? parent) => children[parent].OrderBy(node => node.SortOrder)
            .ThenBy(node => node.StartSequence).ThenBy(node => node.Id);
        var tree = new InternalStructureTree(documentFileId, parseId, Ordered(null).Select(node => Project(node, null)).ToList());
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        return new(InternalStructureReadStatus.Found, tree);
    }

    public async Task<InternalCurrentStructureNode?> ReadCurrentNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var node = await context.DocumentStructureNodes.AsNoTracking().Where(node => node.Id == nodeId)
            .Select(node => new InternalCurrentStructureNode(node.Parse!.DocumentFileId, node.ParseId, node.Id, node.StartSequence, node.EndSequence))
            .SingleOrDefaultAsync(cancellationToken);
        if (node is null || await CurrentParseAsync(context, node.DocumentFileId, cancellationToken) != node.ParseId) return null;
        await transaction.CommitAsync(cancellationToken);
        return node;
    }

    private sealed record NodeRow(Guid Id, Guid LevelId, Guid? ParentId, string Name, int SortOrder,
        int StartSequence, int EndSequence, int? StartPageNumber, int? EndPageNumber);
}
