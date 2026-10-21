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
        return await _context.DocumentStructureNodes
            .AsNoTracking()
            .Where(node => node.ParseId == parseId)
            .OrderBy(node => node.StartSequence)
            .ThenByDescending(node => node.EndSequence)
            .Select(node => MapToModel(node))
            .ToListAsync();
    }

    public async Task<bool> ReplaceAsync(Guid parseId, IReadOnlyList<DocumentStructureNodeModel> nodes)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync();

        // Row lock on the parse: a concurrent save of the same tree waits here until this
        // transaction commits, then replaces the complete result (last complete save wins).
        var locked = await _context.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM document_parses WHERE id = {parseId} FOR UPDATE")
            .ToListAsync();
        if (locked.Count == 0) return false;

        // Audio linked to a node that the new tree no longer contains is unlinked (issue #90);
        // nodes kept by id keep their audio across the delete and re-insert below.
        var keptIds = nodes.Select(node => node.Id).ToList();
        await _context.DocumentStructureAudio
            .Where(audio => audio.ParseId == parseId && audio.NodeId != null && !keptIds.Contains(audio.NodeId.Value))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(audio => audio.NodeId, (Guid?)null)
                .SetProperty(audio => audio.MatchStatus, AudioMatchStatus.NodeRemoved)
                .SetProperty(audio => audio.LinkSource, (string?)null)
                .SetProperty(audio => audio.UpdatedAt, DateTimeOffset.UtcNow));

        await _context.DocumentStructureNodes
            .Where(node => node.ParseId == parseId)
            .ExecuteDeleteAsync();

        // Parents precede children in the list; the insert order keeps the self-reference valid.
        foreach (var node in nodes)
        {
            _context.DocumentStructureNodes.Add(new DocumentStructureNodeEntity
            {
                Id = node.Id,
                ParseId = parseId,
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
        var foreign = await _context.DocumentStructureNodes
            .Where(node => ids.Contains(node.Id) && node.ParseId != parseId)
            .Select(node => node.Id)
            .ToListAsync();
        return [.. foreign];
    }

    public async Task<bool> DocumentFileHasNodesAsync(Guid documentFileId)
    {
        return await _context.DocumentStructureNodes
            .AnyAsync(node => _context.DocumentParses
                .Any(parse => parse.Id == node.ParseId && parse.DocumentFileId == documentFileId));
    }

    private static DocumentStructureNodeModel MapToModel(DocumentStructureNodeEntity entity) => new()
    {
        Id = entity.Id,
        ParseId = entity.ParseId,
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
