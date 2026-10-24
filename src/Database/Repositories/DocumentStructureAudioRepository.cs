using Microsoft.EntityFrameworkCore;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Repositories;

namespace Doctheca.Database.Repositories;

public class DocumentStructureAudioRepository : IDocumentStructureAudioRepository
{
    private readonly DocthecaDbContext _context;
    private int? _leaseBackendPid;

    public DocumentStructureAudioRepository(DocthecaDbContext context)
    {
        _context = context;
    }

    public async Task<IAsyncDisposable> AcquireObjectKeyLeaseAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        if (_leaseBackendPid is not null) throw new InvalidOperationException("An audio key lease is already active.");
        await _context.Database.OpenConnectionAsync(ct);
        var connection = (Npgsql.NpgsqlConnection)_context.Database.GetDbConnection();
        var backend = connection.ProcessID;
        var ordered = keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        try
        {
            foreach (var key in ordered)
            {
                await using var command = new Npgsql.NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended(@key, 132))", connection);
                command.Parameters.AddWithValue("key", key);
                await command.ExecuteNonQueryAsync(ct);
            }
            // Metadata uses this same open session. A lost session cannot commit a new import.
            _leaseBackendPid = backend;
            return new KeyLease(() => ReleaseLeaseAsync(connection, backend, ordered));
        }
        catch { await ReleaseLeaseAsync(connection, backend, ordered); throw; }
    }

    private async ValueTask ReleaseLeaseAsync(Npgsql.NpgsqlConnection connection, int backend, IReadOnlyList<string> keys)
    {
        try
        {
            if (connection.State != System.Data.ConnectionState.Open || connection.ProcessID != backend)
                Npgsql.NpgsqlConnection.ClearPool(connection);
            else foreach (var key in keys.Reverse())
            {
                await using var command = new Npgsql.NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@key, 132))", connection);
                command.Parameters.AddWithValue("key", key);
                await command.ExecuteNonQueryAsync();
            }
        }
        catch { Npgsql.NpgsqlConnection.ClearPool(connection); }
        finally
        {
            _leaseBackendPid = null;
            try { await _context.Database.CloseConnectionAsync(); }
            catch { Npgsql.NpgsqlConnection.ClearPool(connection); }
        }
    }

    private void EnsureLeaseSession()
    {
        if (_leaseBackendPid is { } backend && ((Npgsql.NpgsqlConnection)_context.Database.GetDbConnection()).ProcessID != backend)
            throw new InvalidOperationException("The audio key lease was lost; recovery must be deferred.");
    }

    private sealed class KeyLease(Func<ValueTask> release) : IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0 ? release() : ValueTask.CompletedTask;
    }

    public async Task<List<DocumentStructureAudioModel>> GetByParseIdAsync(Guid parseId)
    {
        var treeId = await StructureOwnership.TreeForParseAsync(_context, parseId);
        return await _context.DocumentStructureAudio
            .AsNoTracking()
            .Where(audio => audio.TreeId == treeId)
            .OrderBy(audio => audio.SourcePath)
            .Select(audio => MapToModel(audio))
            .ToListAsync();
    }

    public async Task<List<DocumentStructureAudioModel>> GetByNodeIdAsync(Guid parseId, Guid nodeId)
    {
        var treeId = await StructureOwnership.TreeForParseAsync(_context, parseId);
        return await _context.DocumentStructureAudio
            .AsNoTracking()
            .Where(audio => audio.TreeId == treeId && audio.NodeId == nodeId)
            .OrderBy(audio => audio.SourcePath)
            .Select(audio => MapToModel(audio))
            .ToListAsync();
    }

    public async Task<DocumentStructureAudioModel?> GetByIdAsync(Guid parseId, Guid audioId)
    {
        var treeId = await StructureOwnership.TreeForParseAsync(_context, parseId);
        var entity = await _context.DocumentStructureAudio
            .AsNoTracking()
            .FirstOrDefaultAsync(audio => audio.TreeId == treeId && audio.Id == audioId);
        return entity == null ? null : MapToModel(entity);
    }

    public async Task<HashSet<string>> GetExistingSourcePathsAsync(Guid parseId, IReadOnlyCollection<string> sourcePaths)
    {
        if (sourcePaths.Count == 0) return [];
        var treeId = await StructureOwnership.TreeForParseAsync(_context, parseId);
        var paths = sourcePaths.ToList();
        var existing = await _context.DocumentStructureAudio
            .Where(audio => audio.TreeId == treeId && paths.Contains(audio.SourcePath))
            .Select(audio => audio.SourcePath)
            .ToListAsync();
        return [.. existing];
    }

    public async Task<bool> InsertAsync(Guid parseId, IReadOnlyList<DocumentStructureAudioModel> items, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        EnsureLeaseSession();
        var file = await StructureOwnership.LockParseOwnerAsync(_context, parseId, cancellationToken);
        if (file is null) return false;
        var treeId = await StructureOwnership.TreeForParseAsync(_context, parseId, cancellationToken);
        if (treeId is null) return false;

        // A structure save that ran after the links were computed may have removed a node.
        var nodeIds = await NodeIdsAsync(treeId, cancellationToken);
        foreach (var item in items)
        {
            var linked = item.NodeId is { } nodeId && nodeIds.Contains(nodeId);
            _context.DocumentStructureAudio.Add(new DocumentStructureAudioEntity
            {
                Id = item.Id,
                ParseId = parseId,
                DocumentFileId = file.Id,
                TreeId = treeId.Value,
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
        EnsureLeaseSession();
        var file = await StructureOwnership.LockParseOwnerAsync(_context, parseId, cancellationToken);
        if (file is null) return new(AudioLinkUpdateStatus.ParseNotFound, []);
        var treeId = await StructureOwnership.TreeForParseAsync(_context, parseId, cancellationToken);

        var audio = await _context.DocumentStructureAudio
            .Where(item => item.TreeId == treeId)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        foreach (var id in changes.Select(change => change.AudioId).Concat(removeIds))
        {
            if (!audio.ContainsKey(id)) return new(AudioLinkUpdateStatus.AudioNotFound, [], id);
        }

        var nodeIds = await NodeIdsAsync(treeId, cancellationToken);
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
        var treeId = await StructureOwnership.TreeForParseAsync(_context, parseId);
        return await _context.DocumentStructureAudio
            .Where(audio => audio.TreeId == treeId)
            .Select(audio => audio.ObjectKey)
            .ToListAsync();
    }

    public async Task<List<string>> GetObjectKeysByDocumentFileIdAsync(Guid documentFileId)
    {
        return await _context.DocumentStructureAudio
            .Where(audio => audio.DocumentFileId == documentFileId)
            .Select(audio => audio.ObjectKey)
            .ToListAsync();
    }

    public async Task<HashSet<string>> GetReferencedObjectKeysAsync(IReadOnlyCollection<string> objectKeys)
    {
        if (objectKeys.Count == 0) return [];
        var keys = objectKeys.ToList();
        await _context.Database.OpenConnectionAsync();
        try
        {
            EnsureLeaseSession();
            var referenced = await _context.DocumentStructureAudio
                .Where(audio => keys.Contains(audio.ObjectKey)).Select(audio => audio.ObjectKey).ToListAsync();
            return [.. referenced];
        }
        finally { await _context.Database.CloseConnectionAsync(); }
    }


    private async Task<HashSet<Guid>> NodeIdsAsync(Guid? treeId, CancellationToken cancellationToken)
    {
        var ids = await _context.DocumentStructureNodes
            .Where(node => node.TreeId == treeId)
            .Select(node => node.Id)
            .ToListAsync(cancellationToken);
        return [.. ids];
    }

    private static DocumentStructureAudioModel MapToModel(DocumentStructureAudioEntity entity) => new()
    {
        Id = entity.Id,
        ParseId = entity.ParseId,
        DocumentFileId = entity.DocumentFileId,
        TreeId = entity.TreeId,
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
