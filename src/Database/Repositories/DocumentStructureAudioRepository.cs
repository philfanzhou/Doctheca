using Microsoft.EntityFrameworkCore;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Repositories;

namespace Doctheca.Database.Repositories;

public partial class DocumentStructureAudioRepository : IDocumentStructureAudioRepository
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

    private static DocumentStructureAudio MapToModel(DocumentStructureAudioEntity entity) => new()
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
