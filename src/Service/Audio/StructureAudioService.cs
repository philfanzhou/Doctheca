using Microsoft.Extensions.Logging;
using Doctheca.Domain.Repositories;

namespace Doctheca.Service.Audio;

public enum AudioResultStatus { Ok, StorageNotConfigured, StorageUnavailable }

/// <summary>The outcome of an object lifecycle operation; never carries storage locations.</summary>
public sealed record AudioResult<T>(AudioResultStatus Status, T? Value = default, string? ErrorCode = null, string? Message = null)
{
    public static AudioResult<T> Ok(T value) => new(AudioResultStatus.Ok, value);
    public static AudioResult<T> Fail(AudioResultStatus status, string code, string message) => new(status, default, code, message);
}

public static class AudioErrorCodes
{
    public const string StorageNotConfigured = "DOCTHECA_AUDIO_STORAGE_NOT_CONFIGURED";
    public const string StorageUnavailable = "DOCTHECA_AUDIO_STORAGE_UNAVAILABLE";
}

public sealed record AudioSweepOutcome(int Scanned, int Deleted, int Failed);

/// <summary>Owns audio object cleanup after database deletion and safe orphan recovery (#107).</summary>
public sealed class StructureAudioService(
    IDocumentStructureAudioRepository audioRepository,
    StructureAudioStore store,
    ILogger<StructureAudioService> logger,
    TimeProvider? timeProvider = null)
{
    internal static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromHours(1);
    private readonly IDocumentStructureAudioRepository _audioRepository = audioRepository;
    private readonly StructureAudioStore _store = store;
    private readonly ILogger<StructureAudioService> _logger = logger;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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
        if (!_store.Status.Available) return AudioResult<AudioSweepOutcome>.Fail(AudioResultStatus.StorageNotConfigured, AudioErrorCodes.StorageNotConfigured,
            "Audio storage is not configured: " + _store.Status.Reason);
        List<Common.Oss.OssObjectInfo> objects;
        try
        {
            objects = await _store.ListAllAsync();
        }
        catch (AudioStorageException)
        {
            return AudioResult<AudioSweepOutcome>.Fail(AudioResultStatus.StorageUnavailable, AudioErrorCodes.StorageUnavailable,
                "Audio storage is unavailable");
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

}
