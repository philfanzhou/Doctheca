using Microsoft.EntityFrameworkCore;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Repositories;

namespace Doctheca.Database.Repositories;

public class DocumentStructureRepository : IDocumentStructureRepository
{
    private readonly DocthecaDbContext _context;

    public DocumentStructureRepository(DocthecaDbContext context)
    {
        _context = context;
    }

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
        if (file is null || !await _context.DocumentParses.AnyAsync(parse => parse.Id == parseId && parse.Status == Doctheca.Domain.Models.DocumentParseStatus.Parsed)) return false;
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
        // Validate again under the common owner lock: pre-lock service validation is advisory.
        if (file.StructureDefinitionId is { } definitionId)
            await _context.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM structure_definitions WHERE id = {definitionId} FOR SHARE").ToListAsync();
        var levels = await _context.StructureLevels.Where(level => level.DefinitionId == file.StructureDefinitionId).ToDictionaryAsync(level => level.Id);
        var sequences = await _context.DocumentParseBlocks.Where(block => block.ParseId == parseId && block.Sequence != null)
            .Select(block => block.Sequence!.Value).ToListAsync();
        var byId = nodes.ToDictionary(node => node.Id);
        if (await _context.DocumentStructureNodes.AnyAsync(node => byId.Keys.Contains(node.Id) && node.TreeId != treeId)) return false;
        foreach (var node in nodes)
        {
            if (!levels.TryGetValue(node.LevelId, out var level) || node.StartSequence is not { } first || node.EndSequence is not { } last ||
                first > last || !sequences.Contains(first) || !sequences.Contains(last)) return false;
            if (node.ParentId is { } parent)
            {
                if (!byId.TryGetValue(parent, out var ancestor) || level.ParentLevelId != ancestor.LevelId ||
                    first < ancestor.StartSequence || last > ancestor.EndSequence) return false;
            }
            else if (level.ParentLevelId is not null) return false;
        }
        foreach (var siblings in nodes.GroupBy(node => node.ParentId))
        {
            var ordered = siblings.OrderBy(node => node.StartSequence).ToArray();
            if (ordered.Zip(ordered.Skip(1)).Any(pair => pair.First.EndSequence >= pair.Second.StartSequence)) return false;
        }
        file.StructureBindingGeneration++;

        // Audio linked to a node that the new tree no longer contains is unlinked (issue #90);
        // nodes kept by id keep their audio across the delete and re-insert below.
        var keptIds = nodes.Select(node => node.Id).ToList();
        await _context.DocumentStructureAudio
            .Where(audio => audio.TreeId == treeId && audio.NodeId != null && !keptIds.Contains(audio.NodeId.Value))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(audio => audio.NodeId, (Guid?)null)
                .SetProperty(audio => audio.MatchStatus, AudioMatchStatus.NodeRemoved)
                .SetProperty(audio => audio.LinkSource, (string?)null)
                .SetProperty(audio => audio.UpdatedAt, DateTimeOffset.UtcNow));

        await _context.DocumentStructureNodes
            .Where(node => node.TreeId == treeId)
            .ExecuteDeleteAsync();

        // Parents precede children in the list; the insert order keeps the self-reference valid.
        foreach (var node in nodes)
        {
            _context.DocumentStructureNodes.Add(new DocumentStructureNodeEntity
            {
                Id = node.Id,
                ParseId = parseId,
                DocumentFileId = file.Id,
                TreeId = treeId.Value,
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

        await _context.SaveChangesAsync();
        await transaction.CommitAsync();
        _context.ChangeTracker.Clear();
        return true;
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

    private static DocumentStructureNodeModel MapToModel(DocumentStructureNodeEntity entity) => new()
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
