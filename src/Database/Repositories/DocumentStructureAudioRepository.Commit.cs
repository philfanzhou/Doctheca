using System.Data;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Services.Structure;
using Microsoft.EntityFrameworkCore;

namespace Doctheca.Database.Repositories;

public partial class DocumentStructureAudioRepository
{
    public async Task<AudioScope?> GetScopeAsync(AudioOwner owner, CancellationToken ct = default)
    {
        await using var tx = await _context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var file = owner.IsDocument
            ? await _context.DocumentFiles.AsNoTracking().SingleOrDefaultAsync(f => f.Id == owner.Id, ct)
            : await _context.DocumentParses.AsNoTracking().Where(p => p.Id == owner.Id).Select(p => p.DocumentFile).SingleOrDefaultAsync(ct);
        return file is null ? null : await ScopeAsync(owner, file, ct);
    }

    private async Task<AudioScope> ScopeAsync(AudioOwner owner, DocumentFileEntity file, CancellationToken ct)
    {
        var treeId = owner.IsDocument ? file.CurrentStructureTreeId : await StructureOwnership.TreeForParseAsync(_context, owner.Id, ct);
        var nodes = await _context.DocumentStructureNodes.AsNoTracking().Where(n => n.TreeId == treeId)
            .OrderBy(n => n.SortOrder).Select(n => DocumentStructureRepository.MapToModel(n)).ToListAsync(ct);
        var levels = await _context.StructureLevels.AsNoTracking().Where(l => l.DefinitionId == file.StructureDefinitionId)
            .Select(l => new StructureLevelModel { Id = l.Id, DefinitionId = l.DefinitionId, ParentLevelId = l.ParentLevelId,
                Name = l.Name, SortOrder = l.SortOrder, Required = l.Required }).ToListAsync(ct);
        return new(file.Id, treeId, owner.IsDocument ? null : owner.Id, treeId == file.CurrentStructureTreeId, nodes, levels);
    }

    public async Task<List<DocumentStructureAudioModel>> GetByOwnerAsync(AudioOwner owner, CancellationToken ct = default)
    {
        var treeId = owner.IsDocument
            ? await _context.DocumentFiles.Where(f => f.Id == owner.Id).Select(f => f.CurrentStructureTreeId).SingleOrDefaultAsync(ct)
            : await StructureOwnership.TreeForParseAsync(_context, owner.Id, ct);
        return await _context.DocumentStructureAudio.AsNoTracking().Where(a => a.TreeId == treeId)
            .OrderBy(a => a.SourcePath).Select(a => MapToModel(a)).ToListAsync(ct);
    }

    public async Task<AudioLinkUpdateResult> CommitAsync(AudioOwner owner, IReadOnlyList<DocumentStructureAudioModel> additions,
        IReadOnlyList<AudioLinkChange> changes, IReadOnlyCollection<Guid> removeIds, DateTimeOffset now,
        bool deleteOnly = false, CancellationToken ct = default)
    {
        await using var tx = await _context.Database.BeginTransactionAsync(ct);
        EnsureLeaseSession();
        var file = owner.IsDocument ? await StructureOwnership.LockDocumentAsync(_context, owner.Id, ct)
            : await StructureOwnership.LockParseOwnerAsync(_context, owner.Id, ct);
        if (file is null) return new(owner.IsDocument ? AudioLinkUpdateStatus.DocumentNotFound : AudioLinkUpdateStatus.ParseNotFound, []);
        // Global order: immutable-key lease (outside transaction), document FOR UPDATE, definition FOR SHARE.
        // Definition writers take only the definition lock and never acquire a document lock after it.
        if (file.StructureDefinitionId is { } definitionId)
            await _context.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM structure_definitions WHERE id = {definitionId} FOR SHARE").ToListAsync(ct);
        var scope = await ScopeAsync(owner, file, ct);
        if (additions.Count > 0 && (!scope.IsCurrent || scope.TreeId is null || scope.Nodes.Count == 0 || additions.Any(a => a.TreeId != scope.TreeId)))
            return new(AudioLinkUpdateStatus.StructureRequired, []);
        var audio = await _context.DocumentStructureAudio.AsNoTracking().Where(a => a.TreeId == scope.TreeId).ToDictionaryAsync(a => a.Id, ct);
        foreach (var id in changes.Select(c => c.AudioId).Concat(removeIds))
            if (!audio.ContainsKey(id)) return new(AudioLinkUpdateStatus.AudioNotFound, [], id);
        var nodes = scope.Nodes.ToDictionary(n => n.Id);
        foreach (var nodeId in changes.Select(c => c.NodeId).Concat(additions.Select(a => a.NodeId)).OfType<Guid>())
            if (!nodes.ContainsKey(nodeId)) return new(AudioLinkUpdateStatus.NodeNotFound, [], nodeId);
        var removed = removeIds.ToHashSet();
        var retained = audio.Values.Where(a => !removed.Contains(a.Id)).Select(MapToModel).ToDictionary(a => a.Id);
        foreach (var change in changes.Where(c => !removed.Contains(c.AudioId)))
        {
            var item = retained[change.AudioId];
            item.NodeId = change.NodeId;
            item.LinkSource = AudioLinkSource.Manual;
            item.UpdatedAt = now;
        }
        var paths = retained.Values.Select(a => a.SourcePath).ToHashSet(StringComparer.Ordinal);
        var duplicate = new List<AudioPolicyError>();
        foreach (var item in additions)
        {
            if (!paths.Add(item.SourcePath)) duplicate.Add(new(item.SourcePath, "is already imported"));
            retained.Add(item.Id, item);
        }
        if (duplicate.Count > 0) return new(AudioLinkUpdateStatus.AlreadyImported, [], Errors: duplicate);
        var policy = new AudioRequiredPolicy(scope.Levels, nodes);
        if (!(deleteOnly && additions.Count == 0 && changes.Count == 0))
        {
            var errors = retained.Values.OrderBy(a => a.SourcePath, StringComparer.Ordinal)
                .Select(a => (a.SourcePath, Reason: policy.Reason(a.NodeId))).Where(a => a.Reason is not null)
                .Select(a => new AudioPolicyError(a.SourcePath, a.Reason!)).ToList();
            if (errors.Count > 0) return new(AudioLinkUpdateStatus.Invalid, [], Errors: errors);
        }
        // A repository caller can already track an older copy. Mutate only the freshly validated row values.
        foreach (var id in changes.Select(c => c.AudioId).Concat(removed).Distinct())
        {
            if (_context.DocumentStructureAudio.Local.FirstOrDefault(a => a.Id == id) is not { } tracked) continue;
            _context.Entry(tracked).CurrentValues.SetValues(audio[id]);
            audio[id] = tracked;
        }
        foreach (var change in changes.Where(c => !removed.Contains(c.AudioId)))
        {
            var entity = audio[change.AudioId];
            _context.Attach(entity);
            entity.NodeId = change.NodeId;
            entity.LinkSource = AudioLinkSource.Manual;
            entity.UpdatedAt = now;
        }
        foreach (var item in additions)
            _context.DocumentStructureAudio.Add(new DocumentStructureAudioEntity { Id = item.Id, DocumentFileId = file.Id,
                TreeId = scope.TreeId!.Value, ParseId = item.ParseId, NodeId = item.NodeId, SourcePath = item.SourcePath,
                ContentType = item.ContentType, SizeBytes = item.SizeBytes, ObjectKey = item.ObjectKey,
                MatchStatus = item.MatchStatus, LinkSource = item.LinkSource, CreatedAt = item.CreatedAt, UpdatedAt = item.UpdatedAt });
        foreach (var id in removed) _context.DocumentStructureAudio.Remove(audio[id]);
        await _context.SaveChangesAsync(ct);
        ct.ThrowIfCancellationRequested();
        await tx.CommitAsync(ct);
        _context.ChangeTracker.Clear();
        return new(AudioLinkUpdateStatus.Ok, removed.Select(id => audio[id].ObjectKey).ToArray());
    }
}
