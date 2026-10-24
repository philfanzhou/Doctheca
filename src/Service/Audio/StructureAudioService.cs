using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Doctheca.Domain.Models;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services.Structure;

namespace Doctheca.Service.Audio;

public enum AudioResultStatus
{
    Ok,
    NotFound,
    Invalid,
    Conflict,
    TooLarge,
    StorageNotConfigured,
    StorageUnavailable,
}

/// <summary>The outcome of an audio operation, mapped by the endpoints to the JSON envelope.</summary>
public sealed record AudioResult<T>(
    AudioResultStatus Status,
    T? Value = default,
    string? ErrorCode = null,
    string? Message = null,
    IReadOnlyList<AudioFileError>? Errors = null)
{
    public static AudioResult<T> Ok(T value) => new(AudioResultStatus.Ok, value);

    public static AudioResult<T> Fail(AudioResultStatus status, string errorCode, string message,
        IReadOnlyList<AudioFileError>? errors = null) => new(status, default, errorCode, message, errors);
}

public static class AudioErrorCodes
{
    public const string ImportInvalid = "DOCTHECA_AUDIO_IMPORT_INVALID";
    public const string ImportTooLarge = "DOCTHECA_AUDIO_IMPORT_TOO_LARGE";
    public const string AlreadyImported = "DOCTHECA_AUDIO_ALREADY_IMPORTED";
    public const string StructureRequired = "DOCTHECA_AUDIO_STRUCTURE_REQUIRED";
    public const string AudioNotFound = "DOCTHECA_AUDIO_NOT_FOUND";
    public const string StorageNotConfigured = "DOCTHECA_AUDIO_STORAGE_NOT_CONFIGURED";
    public const string StorageUnavailable = "DOCTHECA_AUDIO_STORAGE_UNAVAILABLE";
    public const string Invalid = "DOCTHECA_AUDIO_INVALID";
}

/// <summary>The audio of a parse together with the nodes they link to, for projection.</summary>
public sealed record ParseAudio(
    Guid ParseId,
    IReadOnlyList<DocumentStructureAudioModel> Items,
    IReadOnlyDictionary<Guid, DocumentStructureNodeModel> Nodes);

public sealed record AudioImportOutcome(ParseAudio Imported, IReadOnlyList<string> Skipped);

public sealed record AudioPlaybackLink(string Url, DateTimeOffset ExpiresAt, int ExpiresInSeconds);

public sealed record AudioSweepOutcome(int Scanned, int Deleted, int Failed);

/// <summary>
/// Structure audio (issue #90): import with pre-filled node links, manual link changes and
/// removal and cleanup of stored objects when a file, parse or
/// document is deleted.
/// </summary>
public sealed class StructureAudioService
{
    /// <summary>Recovery grace period, in addition to exact-key leases and reference checks.</summary>
    internal static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromHours(1);

    private readonly IDocumentParseRepository _parseRepository;
    private readonly IDocumentStructureRepository _structureRepository;
    private readonly IDocumentStructureAudioRepository _audioRepository;
    private readonly StructureAudioStore _store;
    private readonly ILogger<StructureAudioService> _logger;
    private readonly TimeProvider _timeProvider;

    public StructureAudioService(
        IDocumentParseRepository parseRepository,
        IDocumentStructureRepository structureRepository,
        IDocumentStructureAudioRepository audioRepository,
        StructureAudioStore store,
        ILogger<StructureAudioService> logger,
        TimeProvider? timeProvider = null)
    {
        _parseRepository = parseRepository;
        _structureRepository = structureRepository;
        _audioRepository = audioRepository;
        _store = store;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public DocumentAudioStorageStatus StorageStatus => _store.Status;

    public async Task<bool> ParseExistsAsync(Guid parseId) => await _parseRepository.GetByIdAsync(parseId) != null;

    public async Task<AudioResult<ParseAudio>> ListAsync(Guid parseId)
    {
        if (!await ParseExistsAsync(parseId)) return ParseNotFound<ParseAudio>();
        return AudioResult<ParseAudio>.Ok(await LoadAsync(parseId, await _audioRepository.GetByParseIdAsync(parseId)));
    }

    public async Task<AudioResult<ParseAudio>> ListNodeAsync(Guid parseId, Guid nodeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await ParseExistsAsync(parseId)) return ParseNotFound<ParseAudio>();
        var nodes = await NodesAsync(parseId);
        if (!nodes.ContainsKey(nodeId))
            return AudioResult<ParseAudio>.Fail(AudioResultStatus.NotFound, StructureErrorCodes.NodeNotFound, "Structure node not found");
        var items = await _audioRepository.GetByNodeIdAsync(parseId, nodeId);
        cancellationToken.ThrowIfCancellationRequested();
        return AudioResult<ParseAudio>.Ok(new(parseId, items, nodes));
    }

    public async Task<AudioResult<AudioPlaybackLink>> PlaybackAsync(Guid parseId, Guid audioId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await ParseExistsAsync(parseId)) return ParseNotFound<AudioPlaybackLink>();
        var item = await _audioRepository.GetByIdAsync(parseId, audioId);
        cancellationToken.ThrowIfCancellationRequested();
        if (item == null)
            return AudioResult<AudioPlaybackLink>.Fail(AudioResultStatus.NotFound, AudioErrorCodes.AudioNotFound, "Audio file not found");
        if (!_store.Playback.Available) return StorageUnavailable<AudioPlaybackLink>();
        try
        {
            var link = await _store.GetPlaybackLinkAsync(item.ObjectKey, _timeProvider, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return AudioResult<AudioPlaybackLink>.Ok(link);
        }
        catch (AudioStorageException) { return StorageUnavailable<AudioPlaybackLink>(); }
    }

    public async Task<AudioResult<AudioImportOutcome>> ImportAsync(Guid parseId, AudioImportBatch batch, DocumentAudioOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await ParseExistsAsync(parseId)) return ParseNotFound<AudioImportOutcome>();
        if (!_store.Status.Available) return NotConfigured<AudioImportOutcome>();

        var nodes = await _structureRepository.GetByParseIdAsync(parseId);
        if (nodes.Count == 0)
        {
            return AudioResult<AudioImportOutcome>.Fail(AudioResultStatus.Conflict, AudioErrorCodes.StructureRequired,
                "The parse has no marked structure nodes; mark its structure before importing audio");
        }

        if (batch.TooLarge)
        {
            return AudioResult<AudioImportOutcome>.Fail(AudioResultStatus.TooLarge, AudioErrorCodes.ImportTooLarge,
                $"The import exceeds the {AudioImportReader.FormatBytes(options.MaxArchiveBytes)} limit");
        }
        if (batch.Errors.Count > 0)
        {
            return AudioResult<AudioImportOutcome>.Fail(AudioResultStatus.Invalid, AudioErrorCodes.ImportInvalid,
                "Some files cannot be imported; nothing was stored", batch.Errors);
        }
        if (batch.Entries.Count == 0)
        {
            return AudioResult<AudioImportOutcome>.Fail(AudioResultStatus.Invalid, AudioErrorCodes.ImportInvalid,
                "The import contains no audio files", [new("", "no audio files")]);
        }

        var existing = await _audioRepository.GetExistingSourcePathsAsync(parseId, batch.Entries.Select(entry => entry.SourcePath).ToList());
        if (existing.Count > 0)
        {
            return AudioResult<AudioImportOutcome>.Fail(AudioResultStatus.Conflict, AudioErrorCodes.AlreadyImported,
                "Some files are already imported for this parse; remove them first",
                existing.Order(StringComparer.Ordinal).Select(path => new AudioFileError(path, "is already imported")).ToList());
        }

        var matcher = new StructureAudioMatcher(nodes);
        var now = _timeProvider.GetUtcNow();
        var stored = new List<DocumentStructureAudioModel>();
        var plannedIds = batch.Entries.ToDictionary(entry => entry.SourcePath, _ => Guid.NewGuid());
        var plannedKeys = batch.Entries.Select(entry => $"{StructureAudioStore.ParsePrefix(parseId)}{plannedIds[entry.SourcePath]:N}{entry.Format.Extension}").ToArray();
        await using var lease = await _audioRepository.AcquireObjectKeyLeaseAsync(plannedKeys, cancellationToken);
        try
        {
            foreach (var entry in batch.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = plannedIds[entry.SourcePath];
                using var buffer = new MemoryStream();
                await using (var source = entry.Open())
                {
                    var chunk = new byte[81920];
                    int read;
                    while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
                    {
                        if (read > options.MaxFileBytes - buffer.Length)
                            throw new IOException("Validated audio changed before storage.");
                        await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
                    }
                }
                if (buffer.Length != entry.SizeBytes) throw new IOException("Validated audio changed before storage.");
                buffer.Position = 0;
                var match = matcher.Match(entry.SourcePath);
                // Record the deterministic key before upload, including uncertain upload outcomes.
                var item = new DocumentStructureAudioModel
                {
                    Id = id, ParseId = parseId, NodeId = match.NodeId,
                    SourcePath = entry.SourcePath, ContentType = entry.Format.ContentType,
                    SizeBytes = entry.SizeBytes,
                    ObjectKey = $"{StructureAudioStore.ParsePrefix(parseId)}{id:N}{entry.Format.Extension}",
                    MatchStatus = match.Status,
                    LinkSource = match.NodeId == null ? null : AudioLinkSource.Prefill,
                    CreatedAt = now, UpdatedAt = now,
                };
                stored.Add(item);
                await _store.PutAsync(parseId, id, entry.Format.Extension, buffer, entry.Format.ContentType, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!await _audioRepository.InsertAsync(parseId, stored, cancellationToken))
            {
                await DiscardAsync(parseId, stored);
                return ParseNotFound<AudioImportOutcome>();
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            await DiscardAsync(parseId, stored);
            throw;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_document_structure_audio_tree_id_source_path" })
        {
            await DiscardAsync(parseId, stored);
            return AudioResult<AudioImportOutcome>.Fail(AudioResultStatus.Conflict, AudioErrorCodes.AlreadyImported,
                "Some files are already imported for this parse; remove them first");
        }
        catch (Exception ex)
        {
            await DiscardAsync(parseId, stored);
            _logger.LogWarning("Audio import failed for parse {ParseId}: {ExceptionType}", parseId, ex.GetType().Name);
            return StorageUnavailable<AudioImportOutcome>();
        }

        var saved = await _audioRepository.GetByParseIdAsync(parseId);
        var ids = stored.Select(item => item.Id).ToHashSet();
        var imported = await LoadAsync(parseId, saved.Where(item => ids.Contains(item.Id)).ToList());
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation(
            "Imported {Count} audio files for parse {ParseId}: {Linked} linked, {Flagged} unlinked, {Skipped} skipped",
            stored.Count, parseId, saved.Count(item => ids.Contains(item.Id) && item.NodeId != null),
            saved.Count(item => ids.Contains(item.Id) && item.NodeId == null), batch.Skipped.Count);
        return AudioResult<AudioImportOutcome>.Ok(new AudioImportOutcome(
            imported, batch.Skipped));
    }

    public async Task<AudioResult<ParseAudio>> UpdateAsync(Guid parseId, IReadOnlyList<AudioLinkChange> changes, IReadOnlyCollection<Guid> removeIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _audioRepository.UpdateLinksAsync(parseId, changes, removeIds, _timeProvider.GetUtcNow(), cancellationToken);
        switch (result.Status)
        {
            case AudioLinkUpdateStatus.ParseNotFound:
                return ParseNotFound<ParseAudio>();
            case AudioLinkUpdateStatus.AudioNotFound:
                return AudioResult<ParseAudio>.Fail(AudioResultStatus.NotFound, AudioErrorCodes.AudioNotFound,
                    $"Audio file {result.MissingId} not found");
            case AudioLinkUpdateStatus.NodeNotFound:
                return AudioResult<ParseAudio>.Fail(AudioResultStatus.NotFound, StructureErrorCodes.NodeNotFound,
                    $"Structure node {result.MissingId} not found");
        }

        if (result.RemovedObjectKeys.Count > 0)
        {
            var failed = await DeleteUnreferencedObjectsAsync(result.RemovedObjectKeys);
            if (failed > 0)
                _logger.LogWarning("Removed {Count} audio files of parse {ParseId}; {Failed} objects could not be deleted", result.RemovedObjectKeys.Count, parseId, failed);
        }
        _logger.LogInformation("Audio of parse {ParseId} saved: {Changed} link changes, {Removed} removed", parseId, changes.Count, removeIds.Count);
        var reloaded = await LoadAsync(parseId, await _audioRepository.GetByParseIdAsync(parseId));
        cancellationToken.ThrowIfCancellationRequested();
        return AudioResult<ParseAudio>.Ok(reloaded);
    }

    public Task<List<string>> GetParseObjectKeysAsync(Guid parseId) => _audioRepository.GetObjectKeysByParseIdAsync(parseId);
    public Task<List<string>> GetDocumentObjectKeysAsync(Guid documentId) => _audioRepository.GetObjectKeysByDocumentFileIdAsync(documentId);

    // Compatibility helper; never enumerate an origin prefix or delete a referenced key.
    public Task<int> DeleteParseObjectsAsync(Guid parseId, IReadOnlyCollection<string> keys) => DeleteUnreferencedObjectsAsync(keys);

    public async Task<int> DeleteUnreferencedObjectsAsync(IReadOnlyCollection<string> keys) =>
        (await CleanupAsync(keys)).Failed;

    private async Task<(int Deleted, int Failed)> CleanupAsync(IReadOnlyCollection<string> keys)
    {
        var deleted = 0;
        var failed = 0;
        foreach (var key in keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            try
            {
                await using var lease = await _audioRepository.AcquireObjectKeyLeaseAsync([key]);
                // The lease spans the final reference query and actual object deletion.
                if ((await _audioRepository.GetReferencedObjectKeysAsync([key])).Contains(key)) continue;
                var failures = await _store.DeleteManyAsync([key]);
                failed += failures;
                deleted += 1 - failures;
            }
            catch (Exception) { failed++; _logger.LogWarning("Audio object cleanup deferred."); }
        }
        return (deleted, failed);
    }

    /// <summary>
    /// Deletes audio objects no row references and that are older than the grace period. It is the
    /// repair for deletions whose object cleanup failed and for interrupted imports.
    /// </summary>
    public async Task<AudioResult<AudioSweepOutcome>> SweepOrphansAsync()
    {
        if (!_store.Status.Available) return NotConfigured<AudioSweepOutcome>();
        List<Common.Oss.OssObjectInfo> objects;
        try
        {
            objects = await _store.ListAllAsync();
        }
        catch (AudioStorageException)
        {
            return StorageUnavailable<AudioSweepOutcome>();
        }

        var cutoff = _timeProvider.GetUtcNow() - OrphanGracePeriod;
        var candidates = objects.Where(item => item.LastModified is { } modified && modified < cutoff).Select(item => item.ObjectPath).ToList();
        var cleaned = await CleanupAsync(candidates);
        _logger.LogInformation("Audio orphan sweep: {Scanned} objects scanned, {Deleted} orphans deleted, {Failed} failed",
            objects.Count, cleaned.Deleted, cleaned.Failed);
        return AudioResult<AudioSweepOutcome>.Ok(new AudioSweepOutcome(objects.Count, cleaned.Deleted, cleaned.Failed));
    }

    private async Task DiscardAsync(Guid parseId, IReadOnlyCollection<DocumentStructureAudioModel> stored)
    {
        if (stored.Count == 0) return;
        HashSet<string> referenced;
        try { referenced = await _audioRepository.GetReferencedObjectKeysAsync(stored.Select(item => item.ObjectKey).ToList()); }
        catch (Exception ex)
        {
            // A commit or the reference query may have an uncertain outcome. The grace sweep can
            // recover unreferenced objects later; deleting them now could destroy committed audio.
            _logger.LogWarning("Audio import recovery deferred for parse {ParseId}: {ExceptionType}", parseId, ex.GetType().Name);
            return;
        }
        var failed = await _store.DeleteManyAsync(stored.Select(item => item.ObjectKey).Where(key => !referenced.Contains(key)));
        if (failed > 0)
            _logger.LogWarning("A failed audio import for parse {ParseId} left {Failed} objects; run the audio orphan sweep", parseId, failed);
    }

    private async Task<ParseAudio> LoadAsync(Guid parseId, IReadOnlyList<DocumentStructureAudioModel> items) =>
        new(parseId, items, await NodesAsync(parseId));

    private async Task<Dictionary<Guid, DocumentStructureNodeModel>> NodesAsync(Guid parseId) =>
        (await _structureRepository.GetByParseIdAsync(parseId)).ToDictionary(node => node.Id);

    private static AudioResult<T> ParseNotFound<T>() =>
        AudioResult<T>.Fail(AudioResultStatus.NotFound, StructureErrorCodes.ParseNotFound, "Parse record not found");

    private AudioResult<T> NotConfigured<T>() =>
        AudioResult<T>.Fail(AudioResultStatus.StorageNotConfigured, AudioErrorCodes.StorageNotConfigured,
            "Audio storage is not configured: " + _store.Status.Reason);

    private static AudioResult<T> StorageUnavailable<T>() =>
        AudioResult<T>.Fail(AudioResultStatus.StorageUnavailable, AudioErrorCodes.StorageUnavailable,
            "Audio storage is unavailable");
}
