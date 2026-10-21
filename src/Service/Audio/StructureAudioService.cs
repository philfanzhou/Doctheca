using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
/// removal, node listings, playback links, and cleanup of stored objects when a file, parse or
/// document is deleted.
/// </summary>
public sealed class StructureAudioService
{
    /// <summary>Objects younger than this are never swept, so imports in flight are safe.</summary>
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

    public async Task<AudioResult<ParseAudio>> ListNodeAsync(Guid parseId, Guid nodeId)
    {
        if (!await ParseExistsAsync(parseId)) return ParseNotFound<ParseAudio>();
        var nodes = await NodesAsync(parseId);
        if (!nodes.ContainsKey(nodeId))
            return AudioResult<ParseAudio>.Fail(AudioResultStatus.NotFound, StructureErrorCodes.NodeNotFound, "Structure node not found");
        return AudioResult<ParseAudio>.Ok(new ParseAudio(parseId, await _audioRepository.GetByNodeIdAsync(parseId, nodeId), nodes));
    }

    public async Task<AudioResult<AudioImportOutcome>> ImportAsync(Guid parseId, AudioImportBatch batch, DocumentAudioOptions options)
    {
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
        try
        {
            foreach (var entry in batch.Entries)
            {
                var id = Guid.NewGuid();
                // The storage client needs a known length; one file is at most MaxFileBytes.
                using var buffer = new MemoryStream();
                await using (var source = entry.Open()) await source.CopyToAsync(buffer);
                buffer.Position = 0;
                var key = await _store.PutAsync(parseId, id, entry.Format.Extension, buffer, entry.Format.ContentType);

                var match = matcher.Match(entry.SourcePath);
                stored.Add(new DocumentStructureAudioModel
                {
                    Id = id,
                    ParseId = parseId,
                    NodeId = match.NodeId,
                    SourcePath = entry.SourcePath,
                    ContentType = entry.Format.ContentType,
                    SizeBytes = buffer.Length,
                    ObjectKey = key,
                    MatchStatus = match.Status,
                    LinkSource = match.NodeId == null ? null : AudioLinkSource.Prefill,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }
        catch (AudioStorageException)
        {
            await DiscardAsync(parseId, stored);
            return StorageUnavailable<AudioImportOutcome>();
        }

        bool inserted;
        try
        {
            inserted = await _audioRepository.InsertAsync(parseId, stored);
        }
        catch (DbUpdateException)
        {
            // A concurrent import of the same paths committed first (unique parse + path).
            await DiscardAsync(parseId, stored);
            return AudioResult<AudioImportOutcome>.Fail(AudioResultStatus.Conflict, AudioErrorCodes.AlreadyImported,
                "Some files are already imported for this parse; remove them first");
        }
        catch
        {
            await DiscardAsync(parseId, stored);
            throw;
        }

        if (!inserted)
        {
            await DiscardAsync(parseId, stored);
            return ParseNotFound<AudioImportOutcome>();
        }

        var saved = await _audioRepository.GetByParseIdAsync(parseId);
        var ids = stored.Select(item => item.Id).ToHashSet();
        _logger.LogInformation(
            "Imported {Count} audio files for parse {ParseId}: {Linked} linked, {Flagged} unlinked, {Skipped} skipped",
            stored.Count, parseId, saved.Count(item => ids.Contains(item.Id) && item.NodeId != null),
            saved.Count(item => ids.Contains(item.Id) && item.NodeId == null), batch.Skipped.Count);
        return AudioResult<AudioImportOutcome>.Ok(new AudioImportOutcome(
            await LoadAsync(parseId, saved.Where(item => ids.Contains(item.Id)).ToList()), batch.Skipped));
    }

    public async Task<AudioResult<ParseAudio>> UpdateAsync(Guid parseId, IReadOnlyList<AudioLinkChange> changes, IReadOnlyCollection<Guid> removeIds)
    {
        var result = await _audioRepository.UpdateLinksAsync(parseId, changes, removeIds, _timeProvider.GetUtcNow());
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
            var failed = await _store.DeleteManyAsync(result.RemovedObjectKeys);
            if (failed > 0)
                _logger.LogWarning("Removed {Count} audio files of parse {ParseId}; {Failed} objects could not be deleted", result.RemovedObjectKeys.Count, parseId, failed);
        }
        _logger.LogInformation("Audio of parse {ParseId} saved: {Changed} link changes, {Removed} removed", parseId, changes.Count, removeIds.Count);
        return AudioResult<ParseAudio>.Ok(await LoadAsync(parseId, await _audioRepository.GetByParseIdAsync(parseId)));
    }

    public async Task<AudioResult<AudioPlaybackLink>> GetPlaybackLinkAsync(Guid parseId, Guid audioId)
    {
        if (!await ParseExistsAsync(parseId)) return ParseNotFound<AudioPlaybackLink>();
        var audio = await _audioRepository.GetByIdAsync(parseId, audioId);
        if (audio == null)
            return AudioResult<AudioPlaybackLink>.Fail(AudioResultStatus.NotFound, AudioErrorCodes.AudioNotFound, "Audio file not found");
        if (!_store.Status.Available) return NotConfigured<AudioPlaybackLink>();

        try
        {
            var now = _timeProvider.GetUtcNow();
            var (url, seconds) = await _store.GetPlaybackLinkAsync(audio.ObjectKey);
            return AudioResult<AudioPlaybackLink>.Ok(new AudioPlaybackLink(url, now.AddSeconds(seconds), seconds));
        }
        catch (AudioStorageException)
        {
            return StorageUnavailable<AudioPlaybackLink>();
        }
    }

    /// <summary>The object keys to delete once the parse rows are gone (read before deleting them).</summary>
    public Task<List<string>> GetParseObjectKeysAsync(Guid parseId) => _audioRepository.GetObjectKeysByParseIdAsync(parseId);

    /// <summary>
    /// Deletes a deleted parse's audio objects: the keys its rows referenced and, when storage is
    /// available, anything else under its prefix. Best-effort; returns the number of failures.
    /// </summary>
    public async Task<int> DeleteParseObjectsAsync(Guid parseId, IReadOnlyCollection<string> keys)
    {
        if (keys.Count == 0 && !_store.Status.Available) return 0;
        var failed = _store.Status.Available
            ? await _store.DeleteParseAsync(parseId, keys)
            : await _store.DeleteManyAsync(keys);
        if (failed > 0)
            _logger.LogWarning("Parse {ParseId} deleted but {Failed} audio objects remain; run the audio orphan sweep", parseId, failed);
        return failed;
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
        var orphans = new List<string>();
        foreach (var chunk in candidates.Chunk(500))
        {
            var referenced = await _audioRepository.GetReferencedObjectKeysAsync(chunk);
            orphans.AddRange(chunk.Where(key => !referenced.Contains(key)));
        }

        var failed = await _store.DeleteManyAsync(orphans);
        _logger.LogInformation("Audio orphan sweep: {Scanned} objects scanned, {Deleted} orphans deleted, {Failed} failed",
            objects.Count, orphans.Count - failed, failed);
        return AudioResult<AudioSweepOutcome>.Ok(new AudioSweepOutcome(objects.Count, orphans.Count - failed, failed));
    }

    private async Task DiscardAsync(Guid parseId, IReadOnlyCollection<DocumentStructureAudioModel> stored)
    {
        if (stored.Count == 0) return;
        var failed = await _store.DeleteManyAsync(stored.Select(item => item.ObjectKey));
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
            "Audio storage is unavailable; nothing was stored");
}
