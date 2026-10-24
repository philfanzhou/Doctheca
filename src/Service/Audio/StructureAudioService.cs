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
    Guid? ParseId,
    IReadOnlyList<DocumentStructureAudioModel> Items,
    IReadOnlyDictionary<Guid, DocumentStructureNodeModel> Nodes,
    Guid DocumentFileId = default, Guid? TreeId = null, IReadOnlyList<StructureLevelModel>? Levels = null);

public sealed record AudioImportOutcome(ParseAudio Imported, IReadOnlyList<string> Skipped);
public sealed record AudioPreviewEntry(string SourcePath, string ContentType, long SizeBytes, string MatchStatus,
    Guid? SuggestedNodeId, string? SuggestedNodePath);
public sealed record AudioPreview(IReadOnlyList<AudioPreviewEntry> Items, IReadOnlyList<string> Skipped);

public sealed record AudioPlaybackLink(string Url, DateTimeOffset ExpiresAt, int ExpiresInSeconds);

public sealed record AudioSweepOutcome(int Scanned, int Deleted, int Failed);

/// <summary>
/// Document/tree audio proposals, atomic Required-policy commits and reference-safe object cleanup.
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

    public Task<AudioResult<ParseAudio>> ListAsync(Guid parseId) => ListAsync(new AudioOwner(parseId));
    public async Task<AudioResult<ParseAudio>> ListAsync(AudioOwner owner, CancellationToken ct = default)
    {
        var scope = await _audioRepository.GetScopeAsync(owner, ct);
        if (scope is null) return OwnerMissing<ParseAudio>(owner);
        return AudioResult<ParseAudio>.Ok(Load(scope, await _audioRepository.GetByOwnerAsync(owner, ct)));
    }

    public Task<AudioResult<ParseAudio>> ListNodeAsync(Guid parseId, Guid nodeId, CancellationToken ct) => ListNodeAsync(new AudioOwner(parseId), nodeId, ct);
    public async Task<AudioResult<ParseAudio>> ListNodeAsync(AudioOwner owner, Guid nodeId, CancellationToken ct)
    {
        var result = await ListAsync(owner, ct);
        if (result.Status != AudioResultStatus.Ok) return result;
        if (!result.Value!.Nodes.ContainsKey(nodeId))
            return AudioResult<ParseAudio>.Fail(AudioResultStatus.NotFound, StructureErrorCodes.NodeNotFound, "Structure node not found");
        return AudioResult<ParseAudio>.Ok(result.Value with { Items = result.Value.Items.Where(a => a.NodeId == nodeId).ToArray() });
    }

    public Task<AudioResult<AudioPlaybackLink>> PlaybackAsync(Guid parseId, Guid audioId, CancellationToken ct) => PlaybackAsync(new AudioOwner(parseId), audioId, ct);
    public async Task<AudioResult<AudioPlaybackLink>> PlaybackAsync(AudioOwner owner, Guid audioId, CancellationToken ct)
    {
        var scope = await _audioRepository.GetScopeAsync(owner, ct);
        if (scope is null) return OwnerMissing<AudioPlaybackLink>(owner);
        var item = (await _audioRepository.GetByOwnerAsync(owner, ct)).SingleOrDefault(a => a.Id == audioId);
        if (item is null) return AudioResult<AudioPlaybackLink>.Fail(AudioResultStatus.NotFound, AudioErrorCodes.AudioNotFound, "Audio file not found");
        if (!_store.Playback.Available) return StorageUnavailable<AudioPlaybackLink>();
        try { return AudioResult<AudioPlaybackLink>.Ok(await _store.GetPlaybackLinkAsync(item, _timeProvider, ct)); }
        catch (AudioStorageException) { return StorageUnavailable<AudioPlaybackLink>(); }
    }

    public async Task<AudioResult<AudioScope>> PreflightAsync(AudioOwner owner, CancellationToken ct)
    {
        var scope = await _audioRepository.GetScopeAsync(owner, ct);
        if (scope is null) return OwnerMissing<AudioScope>(owner);
        if (!_store.Status.Available) return NotConfigured<AudioScope>();
        if (!scope.IsCurrent || scope.TreeId is null || scope.Nodes.Count == 0)
            return AudioResult<AudioScope>.Fail(AudioResultStatus.Conflict, AudioErrorCodes.StructureRequired,
                "Import requires the document's current structure with saved nodes; historical trees are read-only for imports");
        return AudioResult<AudioScope>.Ok(scope);
    }

    public async Task<AudioResult<AudioPreview>> PreviewAsync(AudioOwner owner, AudioImportBatch batch, DocumentAudioOptions options, CancellationToken ct)
    {
        var preflight = await PreflightAsync(owner, ct);
        if (preflight.Status != AudioResultStatus.Ok) return CopyFailure<AudioScope, AudioPreview>(preflight);
        var failure = BatchError<AudioPreview>(batch, options);
        if (failure is not null) return failure;
        var scope = preflight.Value!;
        var matcher = new StructureAudioMatcher(scope.Nodes);
        var nodes = scope.Nodes.ToDictionary(n => n.Id);
        var items = batch.Entries.Select(entry =>
        {
            var match = matcher.Match(entry.SourcePath);
            return new AudioPreviewEntry(entry.SourcePath, entry.Format.ContentType, entry.SizeBytes, match.Status,
                match.NodeId, match.NodeId is { } id ? NodePath(nodes[id], nodes) : null);
        }).ToArray();
        ct.ThrowIfCancellationRequested();
        return AudioResult<AudioPreview>.Ok(new(items, batch.Skipped));
    }

    public Task<AudioResult<AudioImportOutcome>> ImportAsync(Guid parseId, AudioImportBatch batch, DocumentAudioOptions options, CancellationToken ct = default) =>
        ImportAsync(new AudioOwner(parseId), batch, options, null, [], [], [], ct);

    public async Task<AudioResult<AudioImportOutcome>> ImportAsync(AudioOwner owner, AudioImportBatch batch, DocumentAudioOptions options,
        Guid? commonTarget, IReadOnlyList<AudioTarget> targets, IReadOnlyList<AudioLinkChange> changes, IReadOnlyCollection<Guid> removeIds, CancellationToken ct = default)
    {
        var preflight = await PreflightAsync(owner, ct);
        if (preflight.Status != AudioResultStatus.Ok) return CopyFailure<AudioScope, AudioImportOutcome>(preflight);
        var failure = BatchError<AudioImportOutcome>(batch, options);
        if (failure is not null) return failure;
        var scope = preflight.Value!;
        var entryPaths = batch.Entries.Select(e => e.SourcePath).ToHashSet(StringComparer.Ordinal);
        var selected = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        var targetErrors = new List<AudioFileError>();
        foreach (var target in targets)
        {
            if (target is null || !AudioImportReader.TryNormalizePath(target.SourcePath, out var path, out _) || !entryPaths.Contains(path))
                targetErrors.Add(new(target?.SourcePath is { } raw ? raw[..Math.Min(raw.Length, AudioImportReader.MaxPathLength)] : "", "does not match an expanded source path in this import"));
            else if (!selected.TryAdd(path, target.NodeId)) targetErrors.Add(new(path, "has more than one target selection"));
        }
        if (targetErrors.Count > 0) return AudioResult<AudioImportOutcome>.Fail(AudioResultStatus.Invalid, AudioErrorCodes.Invalid,
            "Target validation failed; nothing was stored", targetErrors);
        var now = _timeProvider.GetUtcNow();
        var matcher = new StructureAudioMatcher(scope.Nodes);
        var planned = batch.Entries.Select(entry =>
        {
            var id = Guid.NewGuid();
            var match = matcher.Match(entry.SourcePath);
            return new DocumentStructureAudioModel { Id = id, DocumentFileId = scope.DocumentFileId, TreeId = scope.TreeId!.Value,
                ParseId = owner.IsDocument ? null : owner.Id, NodeId = selected.TryGetValue(entry.SourcePath, out var target) ? target : commonTarget,
                SourcePath = entry.SourcePath, ContentType = entry.Format.ContentType, SizeBytes = entry.SizeBytes,
                ObjectKey = $"{(owner.IsDocument ? StructureAudioStore.DocumentPrefix(scope.DocumentFileId) : StructureAudioStore.ParsePrefix(owner.Id))}{id:N}{entry.Format.Extension}",
                MatchStatus = match.Status, LinkSource = AudioLinkSource.Manual, CreatedAt = now, UpdatedAt = now };
        }).ToArray();
        var stored = new List<DocumentStructureAudioModel>();
        AudioLinkUpdateResult committed;
        await using (var lease = await _audioRepository.AcquireObjectKeyLeaseAsync(planned.Select(a => a.ObjectKey).ToArray(), ct))
        {
            try
            {
                for (var i = 0; i < batch.Entries.Count; i++)
                {
                    var entry = batch.Entries[i]; var item = planned[i];
                    using var buffer = new MemoryStream();
                    await using (var source = entry.Open())
                    {
                        var chunk = new byte[81920]; int read;
                        while ((read = await source.ReadAsync(chunk, ct)) > 0)
                        {
                            if (read > options.MaxFileBytes - buffer.Length) throw new IOException("Validated audio changed before storage.");
                            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
                        }
                    }
                    if (buffer.Length != entry.SizeBytes) throw new IOException("Validated audio changed before storage.");
                    buffer.Position = 0;
                    stored.Add(item);
                    if (owner.IsDocument) await _store.PutDocumentAsync(scope.DocumentFileId, item.Id, entry.Format.Extension, buffer, entry.Format.ContentType, ct);
                    else await _store.PutAsync(owner.Id, item.Id, entry.Format.Extension, buffer, entry.Format.ContentType, ct);
                }
                committed = await _audioRepository.CommitAsync(owner, planned, changes, removeIds, now, ct: ct);
                if (committed.Status != AudioLinkUpdateStatus.Ok)
                {
                    await DiscardAsync(owner.Id, stored);
                    return CommitFailure<AudioImportOutcome>(owner, committed);
                }
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { await DiscardAsync(owner.Id, stored); throw; }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_document_structure_audio_tree_id_source_path" })
            {
                await DiscardAsync(owner.Id, stored);
                return AudioResult<AudioImportOutcome>.Fail(AudioResultStatus.Conflict, AudioErrorCodes.AlreadyImported, "Some files are already imported; nothing was changed");
            }
            catch (Exception ex)
            {
                await DiscardAsync(owner.Id, stored);
                _logger.LogWarning("Audio import failed for owner {OwnerId}: {ExceptionType}", owner.Id, ex.GetType().Name);
                return StorageUnavailable<AudioImportOutcome>();
            }
        }
        // Removed objects take their own exact-key leases only after releasing upload leases and owner locks.
        await DeleteUnreferencedObjectsAsync(committed.RemovedObjectKeys);
        var reloaded = await ListAsync(owner, ct);
        if (reloaded.Status != AudioResultStatus.Ok) return CopyFailure<ParseAudio, AudioImportOutcome>(reloaded);
        var ids = planned.Select(a => a.Id).ToHashSet();
        return AudioResult<AudioImportOutcome>.Ok(new(reloaded.Value! with { Items = reloaded.Value!.Items.Where(a => ids.Contains(a.Id)).ToArray() }, batch.Skipped));
    }

    public Task<AudioResult<ParseAudio>> UpdateAsync(Guid parseId, IReadOnlyList<AudioLinkChange> changes, IReadOnlyCollection<Guid> removeIds, CancellationToken ct = default) =>
        UpdateAsync(new AudioOwner(parseId), changes, removeIds, false, ct);
    public async Task<AudioResult<ParseAudio>> UpdateAsync(AudioOwner owner, IReadOnlyList<AudioLinkChange> changes, IReadOnlyCollection<Guid> removeIds,
        bool deleteOnly, CancellationToken ct = default)
    {
        var result = await _audioRepository.CommitAsync(owner, [], changes, removeIds, _timeProvider.GetUtcNow(), deleteOnly, ct);
        if (result.Status != AudioLinkUpdateStatus.Ok) return CommitFailure<ParseAudio>(owner, result);
        await DeleteUnreferencedObjectsAsync(result.RemovedObjectKeys);
        return await ListAsync(owner, ct);
    }

    private static AudioResult<T> CommitFailure<T>(AudioOwner owner, AudioLinkUpdateResult result) => result.Status switch
    {
        AudioLinkUpdateStatus.ParseNotFound or AudioLinkUpdateStatus.DocumentNotFound => OwnerMissing<T>(owner),
        AudioLinkUpdateStatus.AudioNotFound => AudioResult<T>.Fail(AudioResultStatus.NotFound, AudioErrorCodes.AudioNotFound, "Audio file not found"),
        AudioLinkUpdateStatus.NodeNotFound => AudioResult<T>.Fail(AudioResultStatus.NotFound, StructureErrorCodes.NodeNotFound, "Structure node not found"),
        AudioLinkUpdateStatus.StructureRequired => AudioResult<T>.Fail(AudioResultStatus.Conflict, AudioErrorCodes.StructureRequired, "Import requires the current structure with saved nodes"),
        AudioLinkUpdateStatus.AlreadyImported => AudioResult<T>.Fail(AudioResultStatus.Conflict, AudioErrorCodes.AlreadyImported, "Some files are already imported; nothing was changed", result.Errors?.Select(e => new AudioFileError(e.File, e.Reason)).ToArray()),
        _ => AudioResult<T>.Fail(AudioResultStatus.Invalid, AudioErrorCodes.Invalid, "Audio policy validation failed; nothing was changed", result.Errors?.Select(e => new AudioFileError(e.File, e.Reason)).ToArray()),
    };
    private static AudioResult<T>? BatchError<T>(AudioImportBatch batch, DocumentAudioOptions options) =>
        batch.TooLarge ? AudioResult<T>.Fail(AudioResultStatus.TooLarge, AudioErrorCodes.ImportTooLarge, $"The import exceeds the {AudioImportReader.FormatBytes(options.MaxArchiveBytes)} limit") :
        batch.Errors.Count > 0 ? AudioResult<T>.Fail(AudioResultStatus.Invalid, AudioErrorCodes.ImportInvalid, "Some files cannot be imported; nothing was stored", batch.Errors) :
        batch.Entries.Count == 0 ? AudioResult<T>.Fail(AudioResultStatus.Invalid, AudioErrorCodes.ImportInvalid, "The import contains no audio files", [new("", "no audio files")]) : null;
    private static AudioResult<TOut> CopyFailure<TIn, TOut>(AudioResult<TIn> result) => AudioResult<TOut>.Fail(result.Status, result.ErrorCode!, result.Message!, result.Errors);
    private static AudioResult<T> OwnerMissing<T>(AudioOwner owner) => AudioResult<T>.Fail(AudioResultStatus.NotFound,
        owner.IsDocument ? StructureErrorCodes.FileNotFound : StructureErrorCodes.ParseNotFound, owner.IsDocument ? "File not found" : "Parse record not found");
    private static ParseAudio Load(AudioScope scope, IReadOnlyList<DocumentStructureAudioModel> items) =>
        new(scope.ParseId, items, scope.Nodes.ToDictionary(n => n.Id), scope.DocumentFileId, scope.TreeId, scope.Levels);
    internal static string NodePath(DocumentStructureNodeModel node, IReadOnlyDictionary<Guid, DocumentStructureNodeModel> nodes)
    {
        var names = new List<string>(); var seen = new HashSet<Guid>();
        for (var cursor = node; cursor is not null && seen.Add(cursor.Id); cursor = cursor.ParentId is { } parent ? nodes.GetValueOrDefault(parent) : null) names.Insert(0, cursor.Name);
        return string.Join(" > ", names);
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

    private AudioResult<T> NotConfigured<T>() =>
        AudioResult<T>.Fail(AudioResultStatus.StorageNotConfigured, AudioErrorCodes.StorageNotConfigured,
            "Audio storage is not configured: " + _store.Status.Reason);

    private static AudioResult<T> StorageUnavailable<T>() =>
        AudioResult<T>.Fail(AudioResultStatus.StorageUnavailable, AudioErrorCodes.StorageUnavailable,
            "Audio storage is unavailable");
}
