using Microsoft.EntityFrameworkCore;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Services.Structure;
using Doctheca.Domain.Repositories;

namespace Doctheca.Database.Repositories;

public class DocumentStructureRepository : IDocumentStructureRepository
{
    private readonly DocthecaDbContext _context;

    public DocumentStructureRepository(DocthecaDbContext context)
    {
        _context = context;
    }

    public Task<List<DocumentStructureNodeModel>> GetCurrentBindingsAsync(Guid parseId) =>
        (from node in _context.DocumentStructureNodes.AsNoTracking()
         join file in _context.DocumentFiles on node.DocumentFileId equals file.Id
         where node.TreeId == file.CurrentStructureTreeId && node.ParseId == parseId
         orderby node.SortOrder
         select MapToModel(node)).ToListAsync();

    public async Task<List<DocumentStructureNodeModel>> GetByParseIdAsync(Guid parseId)
    {
        var treeId = await StructureOwnership.TreeForParseAsync(_context, parseId);
        return await _context.DocumentStructureNodes
            .AsNoTracking()
            .Where(node => node.TreeId == treeId)
            .OrderBy(node => node.StartSequence)
            .ThenByDescending(node => node.EndSequence)
            .Select(node => MapToModel(node))
            .ToListAsync();
    }

    public async Task<bool> ReplaceAsync(Guid parseId, IReadOnlyList<DocumentStructureNodeModel> nodes)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync();

        var file = await StructureOwnership.LockParseOwnerAsync(_context, parseId);
        if (file is null || !await _context.DocumentParses.AnyAsync(parse => parse.Id == parseId && parse.Status == DocumentParseStatus.Parsed)) return false;
        var treeId = await StructureOwnership.TreeForParseAsync(_context, parseId);
        if (treeId is null)
        {
            var tree = new DocumentStructureTreeEntity { DocumentFileId = file.Id, OriginParseId = parseId };
            _context.DocumentStructureTrees.Add(tree);
            await _context.SaveChangesAsync();
            treeId = tree.Id;
            file.CurrentStructureTreeId ??= tree.Id;
        }
        file.CurrentStructureTreeId ??= treeId.Value;
        // Legacy saves stamp their real parse identity; the document adapter supplies optional identities.
        foreach (var node in nodes) node.ParseId = parseId;
        if ((await ValidateNodesAsync(_context, file, treeId, nodes, CancellationToken.None)).Count > 0) return false;
        if (treeId == file.CurrentStructureTreeId) file.StructureBindingGeneration++;

        await ReplaceNodesAsync(_context, file.Id, treeId.Value, nodes, CancellationToken.None);

        await _context.SaveChangesAsync();
        await transaction.CommitAsync();
        _context.ChangeTracker.Clear();
        return true;
    }

    internal static async Task<List<StructureFieldError>> ValidateNodesAsync(
        DocthecaDbContext context, DocumentFileEntity file, Guid? treeId, IReadOnlyList<DocumentStructureNodeModel> nodes, CancellationToken ct, IReadOnlyDictionary<Guid, string>? paths = null)
    {
        var errors = new List<StructureFieldError>();
        if (file.StructureDefinitionId is { } definitionId)
            await context.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM structure_definitions WHERE id = {definitionId} FOR SHARE").ToListAsync(ct);
        var levels = await context.StructureLevels.Where(l => l.DefinitionId == file.StructureDefinitionId).ToDictionaryAsync(l => l.Id, ct);
        var ids = nodes.Select(n => n.Id).ToArray();
        if (nodes.Count > 5000 || ids.Distinct().Count() != ids.Length) errors.Add(new("nodes", "at most 5000 uniquely identified nodes are allowed"));
        foreach (var foreign in await context.DocumentStructureNodes.Where(n => ids.Contains(n.Id) && n.TreeId != treeId).Select(n => n.Id).ToListAsync(ct))
            errors.Add(new((paths?.GetValueOrDefault(foreign) ?? "nodes") + ".id", "node ids cannot come from another book or a historical tree"));
        var parseIds = nodes.Where(n => n.ParseId.HasValue).Select(n => n.ParseId!.Value).Distinct().ToArray();
        if (parseIds.Length > 1) errors.Add(new("nodes", "one tree cannot bind multiple parses"));
        HashSet<int> sequences = [];
        if (parseIds.Length == 1)
        {
            var parseId = parseIds[0];
            if (!await context.DocumentParses.AnyAsync(p => p.Id == parseId && p.DocumentFileId == file.Id && p.Status == DocumentParseStatus.Parsed, ct))
                errors.Add(new("nodes", "the binding must reference a completed parse of this book"));
            else sequences = (await context.DocumentParseBlocks.Where(b => b.ParseId == parseId && b.Sequence != null).Select(b => b.Sequence!.Value).ToListAsync(ct)).ToHashSet();
        }
        var byId = nodes.DistinctBy(n => n.Id).ToDictionary(n => n.Id);
        foreach (var node in nodes)
        {
            var path = paths?.GetValueOrDefault(node.Id) ?? "nodes";
            if (string.IsNullOrWhiteSpace(node.Name) || node.Name.Length > 500) errors.Add(new(path + ".name", "names must contain 1 to 500 characters"));
            var parent = node.ParentId is { } parentId && byId.TryGetValue(parentId, out var found) ? found : null;
            if (!levels.TryGetValue(node.LevelId, out var level) || level.ParentLevelId != parent?.LevelId || (node.ParentId.HasValue && parent is null))
                errors.Add(new(path + ".levelId", "levels must belong to the definition and match the parent hierarchy"));
            if (node.ParseId.HasValue != node.StartSequence.HasValue || node.ParseId.HasValue != node.EndSequence.HasValue)
                errors.Add(new(path + ".parseId", "parseId, startSequence and endSequence must all be present or all absent"));
            if (!node.ParseId.HasValue) continue;
            if (node.StartSequence is not { } start || node.EndSequence is not { } end || start > end || !sequences.Contains(start) || !sequences.Contains(end))
                errors.Add(new(path + ".startSequence", "binding endpoints must exist and startSequence must not exceed endSequence"));
            if (parent is not null && (parent.ParseId != node.ParseId || parent.StartSequence is null || node.StartSequence < parent.StartSequence || node.EndSequence > parent.EndSequence))
                errors.Add(new(path + ".startSequence", "a bound child requires the same bound parent and a contained range"));
        }
        foreach (var siblings in nodes.Where(n => n.ParseId.HasValue).GroupBy(n => n.ParentId))
        {
            var ordered = siblings.OrderBy(n => n.StartSequence).ToArray();
            if (ordered.Zip(ordered.Skip(1)).Any(pair => pair.First.EndSequence >= pair.Second.StartSequence))
                errors.Add(new("nodes", "bound siblings cannot overlap"));
        }
        return errors;
    }

    internal static async Task ReplaceNodesAsync(DocthecaDbContext context, Guid documentId, Guid treeId,
        IReadOnlyList<DocumentStructureNodeModel> nodes, CancellationToken ct)
    {
        // Audio linked to a node that the new tree no longer contains is unlinked (issue #90);
        // nodes kept by id keep their audio across the delete and re-insert below.
        var keptIds = nodes.Select(node => node.Id).ToList();
        await context.DocumentStructureAudio
            .Where(audio => audio.TreeId == treeId && audio.NodeId != null && !keptIds.Contains(audio.NodeId.Value))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(audio => audio.NodeId, (Guid?)null)
                .SetProperty(audio => audio.MatchStatus, AudioMatchStatus.NodeRemoved)
                .SetProperty(audio => audio.LinkSource, (string?)null)
                .SetProperty(audio => audio.UpdatedAt, DateTimeOffset.UtcNow), ct);

        await context.DocumentStructureNodes
            .Where(node => node.TreeId == treeId)
            .ExecuteDeleteAsync(ct);

        // Parents precede children in the list; the insert order keeps the self-reference valid.
        foreach (var node in nodes)
        {
            context.DocumentStructureNodes.Add(new DocumentStructureNodeEntity
            {
                Id = node.Id,
                ParseId = node.ParseId,
                DocumentFileId = documentId,
                TreeId = treeId,
                LevelId = node.LevelId,
                ParentId = node.ParentId,
                Name = node.Name,
                SortOrder = node.SortOrder,
                StartSequence = node.StartSequence,
                EndSequence = node.EndSequence,
                CreatedAt = node.CreatedAt,
                UpdatedAt = node.UpdatedAt,
            });
        }
    }

    public async Task<HashSet<Guid>> GetForeignNodeIdsAsync(Guid parseId, IReadOnlyCollection<Guid> nodeIds)
    {
        if (nodeIds.Count == 0) return [];
        var ids = nodeIds.ToList();
        var treeId = await StructureOwnership.TreeForParseAsync(_context, parseId);
        var foreign = await _context.DocumentStructureNodes
            .Where(node => ids.Contains(node.Id) && node.TreeId != treeId)
            .Select(node => node.Id)
            .ToListAsync();
        return [.. foreign];
    }

    public async Task<bool> DocumentFileHasNodesAsync(Guid documentFileId)
    {
        return await _context.DocumentStructureNodes
            .AnyAsync(node => node.DocumentFileId == documentFileId);
    }

    internal static DocumentStructureNodeModel MapToModel(DocumentStructureNodeEntity entity) => new()
    {
        Id = entity.Id,
        ParseId = entity.ParseId,
        DocumentFileId = entity.DocumentFileId,
        TreeId = entity.TreeId,
        LevelId = entity.LevelId,
        ParentId = entity.ParentId,
        Name = entity.Name,
        SortOrder = entity.SortOrder,
        StartSequence = entity.StartSequence,
        EndSequence = entity.EndSequence,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
    };
}
