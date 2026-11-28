using Microsoft.Extensions.Logging;
using Doctheca.Common.ObjectStorage;
using Doctheca.Domain.Models;
using Doctheca.Domain.Models.Structure;

namespace Doctheca.Service.Audio;

/// <summary>Raised for any object storage failure; carries no SDK message, endpoint or key.</summary>
public sealed class AudioStorageException() : Exception("Audio storage is unavailable.");

/// <summary>
/// Structure audio objects in the existing S3-compatible storage (<see cref="IObjectStore"/>),
/// always under <see cref="DocumentAudioOptions.ObjectPrefix"/> as
/// <c>audio/documents/{documentId}/{audioId}{extension}</c> or the retained legacy parse prefix. Keys are exposed only inside an authorized short-lived public playback URL.
/// </summary>
public sealed partial class StructureAudioStore
{
    private readonly Lazy<IObjectStore> _objectStore;
    private readonly ILogger<StructureAudioStore> _logger;

    public StructureAudioStore(
        Func<IObjectStore> objectStore,
        DocumentAudioStorageStatus status,
        ILogger<StructureAudioStore> logger,
        DocumentAudioPlaybackSettings? playback = null)
    {
        _objectStore = new Lazy<IObjectStore>(objectStore);
        Status = status;
        _logger = logger;
        Playback = playback ?? new(false, 300);
    }

    public DocumentAudioStorageStatus Status { get; }

    public DocumentAudioPlaybackSettings Playback { get; }

    /// <summary>Checks bytes internally before signing through the separate public client.</summary>
    public async Task<AudioPlaybackLink> GetPlaybackLinkAsync(string key, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Playback.Available) throw new AudioStorageException();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            EnsureAudioKey(key);
            if (!await _objectStore.Value.ObjectExistsAsync(key, timeout.Token)) throw new AudioStorageException();
            timeout.Token.ThrowIfCancellationRequested();
            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(timeProvider.GetUtcNow().ToUnixTimeSeconds());
            var url = await _objectStore.Value.GetPresignedUrlAsync(key, Playback.LinkSeconds, issuedAt, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            return new(url, issuedAt.AddSeconds(Playback.LinkSeconds), Playback.LinkSeconds);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Audio playback storage failed: {ExceptionType}", ex.GetType().Name);
            throw new AudioStorageException();
        }
    }

    internal static string ParsePrefix(Guid parseId) => $"{DocumentAudioOptions.ObjectPrefix}{parseId:N}/";
    internal static string DocumentPrefix(Guid documentId) => $"{DocumentAudioOptions.ObjectPrefix}documents/{documentId:N}/";

    public Task<AudioPlaybackLink> GetPlaybackLinkAsync(DocumentStructureAudio item, TimeProvider clock, CancellationToken ct)
    {
        if (!MatchesIdentity(item.DocumentFileId, item.ParseId, item.Id, item.ObjectKey, item.ContentType)) throw new AudioStorageException();
        return GetPlaybackLinkAsync(item.ObjectKey, clock, ct);
    }

    internal static bool MatchesIdentity(Guid documentId, Guid? originParseId, Guid audioId, string key, string contentType)
    {
        var extension = Path.GetExtension(key);
        var type = extension switch { ".mp3" => "audio/mpeg", ".m4a" => "audio/mp4", ".wav" => "audio/wav",
            ".ogg" => "audio/ogg", ".flac" => "audio/flac", _ => null };
        return type is not null && contentType == type &&
            (key == $"{DocumentPrefix(documentId)}{audioId:N}{extension}" ||
             originParseId is { } parseId && key == $"{ParsePrefix(parseId)}{audioId:N}{extension}");
    }

    public Task<string> PutDocumentAsync(Guid documentId, Guid audioId, string extension, Stream content, string contentType, CancellationToken ct = default) =>
        PutCoreAsync(DocumentPrefix(documentId), audioId, extension, content, contentType, ct);

    /// <summary>Stores one file and returns its object key.</summary>
    public Task<string> PutAsync(Guid parseId, Guid audioId, string extension, Stream content, string contentType, CancellationToken cancellationToken = default) =>
        PutCoreAsync(ParsePrefix(parseId), audioId, extension, content, contentType, cancellationToken);

    private async Task<string> PutCoreAsync(string prefix, Guid audioId, string extension, Stream content, string contentType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Status.Available) throw new AudioStorageException();
        var key = $"{prefix}{audioId:N}{extension}";
        try
        {
            await _objectStore.Value.UploadAsync(key, content, contentType, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // SDK exceptions can carry endpoints, keys or signed URLs; only the type is logged.
            _logger.LogWarning("Audio object upload failed for audio {AudioId}: {ExceptionType}", audioId, ex.GetType().Name);
            throw new AudioStorageException();
        }
        return key;
    }

    /// <summary>Deletes the given audio objects; returns how many could not be deleted.</summary>
    public async Task<int> DeleteManyAsync(IEnumerable<string> keys)
    {
        var failed = 0;
        foreach (var key in keys)
        {
            if (!await DeleteAsync(key)) failed++;
        }
        return failed;
    }

    /// <summary>Every stored audio object, for the orphan sweep.</summary>
    public async Task<List<ObjectInfo>> ListAllAsync()
    {
        try
        {
            return (await _objectStore.Value.ListObjectsOrThrowAsync(DocumentAudioOptions.ObjectPrefix))
                .Where(item => item.ObjectPath.StartsWith(DocumentAudioOptions.ObjectPrefix, StringComparison.Ordinal))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Listing audio objects failed: {ExceptionType}", ex.GetType().Name);
            throw new AudioStorageException();
        }
    }

    private async Task<bool> DeleteAsync(string key)
    {
        EnsureAudioKey(key);
        try
        {
            return await _objectStore.Value.DeleteAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Audio object delete failed: {ExceptionType}", ex.GetType().Name);
            return false;
        }
    }

    // Defence in depth: this store never touches legacy upload objects or parse images.
    private static void EnsureAudioKey(string key)
    {
        if (!key.StartsWith(DocumentAudioOptions.ObjectPrefix, StringComparison.Ordinal) || key.Contains(".."))
            throw new InvalidOperationException("Not an audio object key.");
    }
}
