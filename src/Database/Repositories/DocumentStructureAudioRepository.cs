using Microsoft.EntityFrameworkCore;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Repositories;

namespace Doctheca.Database.Repositories;

public class DocumentStructureAudioRepository : IDocumentStructureAudioRepository
{
    private readonly DocthecaDbContext _context;

    public DocumentStructureAudioRepository(DocthecaDbContext context)
    {
        _context = context;
    }

    public async Task<List<DocumentStructureAudioModel>> GetByParseIdAsync(Guid parseId)
    {
        return await _context.DocumentStructureAudio
            .AsNoTracking()
            .Where(audio => audio.ParseId == parseId)
            .OrderBy(audio => audio.SourcePath)
            .Select(audio => MapToModel(audio))
            .ToListAsync();
    }

    public async Task<List<DocumentStructureAudioModel>> GetByNodeIdAsync(Guid parseId, Guid nodeId)
    {
        return await _context.DocumentStructureAudio
            .AsNoTracking()
            .Where(audio => audio.ParseId == parseId && audio.NodeId == nodeId)
            .OrderBy(audio => audio.SourcePath)
            .Select(audio => MapToModel(audio))
            .ToListAsync();
    }

    public async Task<DocumentStructureAudioModel?> GetByIdAsync(Guid parseId, Guid audioId)
    {
        var entity = await _context.DocumentStructureAudio
            .AsNoTracking()
            .FirstOrDefaultAsync(audio => audio.ParseId == parseId && audio.Id == audioId);
        return entity == null ? null : MapToModel(entity);
    }

    public async Task<HashSet<string>> GetExistingSourcePathsAsync(Guid parseId, IReadOnlyCollection<string> sourcePaths)
    {
        if (sourcePaths.Count == 0) return [];
        var paths = sourcePaths.ToList();
        var existing = await _context.DocumentStructureAudio
            .Where(audio => audio.ParseId == parseId && paths.Contains(audio.SourcePath))
            .Select(audio => audio.SourcePath)
            .ToListAsync();
        return [.. existing];
    }

    public async Task<bool> InsertAsync(Guid parseId, IReadOnlyList<DocumentStructureAudioModel> items, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockParseAsync(parseId, cancellationToken)) return false;

        // A structure save that ran after the links were computed may have removed a node.
        var nodeIds = await NodeIdsAsync(parseId, cancellationToken);
        foreach (var item in items)
        {
            var linked = item.NodeId is { } nodeId && nodeIds.Contains(nodeId);
            _context.DocumentStructureAudio.Add(new DocumentStructureAudioEntity
            {
                Id = item.Id,
                ParseId = parseId,
                NodeId = linked ? item.NodeId : null,
                SourcePath = item.SourcePath,
                ContentType = item.ContentType,
                SizeBytes = item.SizeBytes,
                ObjectKey = item.ObjectKey,
                MatchStatus = item.NodeId != null && !linked ? AudioMatchStatus.NodeRemoved : item.MatchStatus,
                LinkSource = linked ? item.LinkSource : null,
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt,
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        _context.ChangeTracker.Clear();
        return true;
    }

    public async Task<AudioLinkUpdateResult> UpdateLinksAsync(Guid parseId, IReadOnlyList<AudioLinkChange> changes,
        IReadOnlyCollection<Guid> removeIds, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockParseAsync(parseId, cancellationToken)) return new(AudioLinkUpdateStatus.ParseNotFound, []);

        var audio = await _context.DocumentStructureAudio
            .Where(item => item.ParseId == parseId)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        foreach (var id in changes.Select(change => change.AudioId).Concat(removeIds))
        {
            if (!audio.ContainsKey(id)) return new(AudioLinkUpdateStatus.AudioNotFound, [], id);
        }

        var nodeIds = await NodeIdsAsync(parseId, cancellationToken);
        foreach (var change in changes)
        {
            if (change.NodeId is { } nodeId && !nodeIds.Contains(nodeId))
                return new(AudioLinkUpdateStatus.NodeNotFound, [], nodeId);
        }

        var remove = removeIds.ToHashSet();
        foreach (var change in changes.Where(change => !remove.Contains(change.AudioId)))
        {
            var entity = audio[change.AudioId];
            if (entity.NodeId == change.NodeId) continue;
            entity.NodeId = change.NodeId;
            entity.LinkSource = AudioLinkSource.Manual;
            entity.UpdatedAt = now;
        }

        var removedKeys = new List<string>();
        foreach (var id in remove)
        {
            removedKeys.Add(audio[id].ObjectKey);
            _context.DocumentStructureAudio.Remove(audio[id]);
        }

        await _context.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        _context.ChangeTracker.Clear();
        return new(AudioLinkUpdateStatus.Ok, removedKeys);
    }

    public async Task<List<string>> GetObjectKeysByParseIdAsync(Guid parseId)
    {
        return await _context.DocumentStructureAudio
            .Where(audio => audio.ParseId == parseId)
            .Select(audio => audio.ObjectKey)
            .ToListAsync();
    }

    public async Task<List<string>> GetObjectKeysByDocumentFileIdAsync(Guid documentFileId)
    {
        return await _context.DocumentStructureAudio
            .Where(audio => _context.DocumentParses
                .Any(parse => parse.Id == audio.ParseId && parse.DocumentFileId == documentFileId))
            .Select(audio => audio.ObjectKey)
            .ToListAsync();
    }

    public async Task<HashSet<string>> GetReferencedObjectKeysAsync(IReadOnlyCollection<string> objectKeys)
    {
        if (objectKeys.Count == 0) return [];
        var keys = objectKeys.ToList();
        var referenced = await _context.DocumentStructureAudio
            .Where(audio => keys.Contains(audio.ObjectKey))
            .Select(audio => audio.ObjectKey)
            .ToListAsync();
        return [.. referenced];
    }

    // The same parse row lock as DocumentStructureRepository.ReplaceAsync, so links and the
    // node tree they point into never change concurrently.
    private async Task<bool> LockParseAsync(Guid parseId, CancellationToken cancellationToken)
    {
        var locked = await _context.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM document_parses WHERE id = {parseId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return locked.Count > 0;
    }

    private async Task<HashSet<Guid>> NodeIdsAsync(Guid parseId, CancellationToken cancellationToken)
    {
        var ids = await _context.DocumentStructureNodes
            .Where(node => node.ParseId == parseId)
            .Select(node => node.Id)
            .ToListAsync(cancellationToken);
        return [.. ids];
    }

    private static DocumentStructureAudioModel MapToModel(DocumentStructureAudioEntity entity) => new()
    {
        Id = entity.Id,
        ParseId = entity.ParseId,
        NodeId = entity.NodeId,
        SourcePath = entity.SourcePath,
        ContentType = entity.ContentType,
        SizeBytes = entity.SizeBytes,
        ObjectKey = entity.ObjectKey,
        MatchStatus = entity.MatchStatus,
        LinkSource = entity.LinkSource,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
    };
}
