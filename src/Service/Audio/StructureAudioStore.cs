using Microsoft.Extensions.Logging;
using Doctheca.Common.Oss;
using Doctheca.Domain.Models;

namespace Doctheca.Service.Audio;

/// <summary>Raised for any object storage failure; carries no SDK message, endpoint or key.</summary>
public sealed class AudioStorageException() : Exception("Audio storage is unavailable.");

/// <summary>
/// Structure audio objects in the existing S3-compatible storage (<see cref="IOssService"/>),
/// always under <see cref="DocumentAudioOptions.ObjectPrefix"/> as
/// <c>audio/{parseId}/{audioId}{extension}</c>. Keys are exposed only inside an authorized short-lived public playback URL.
/// </summary>
public sealed partial class StructureAudioStore
{
    private readonly Lazy<IOssService> _ossService;
    private readonly ILogger<StructureAudioStore> _logger;

    public StructureAudioStore(
        Func<IOssService> ossService,
        DocumentAudioStorageStatus status,
        ILogger<StructureAudioStore> logger,
        DocumentAudioPlaybackSettings? playback = null)
    {
        _ossService = new Lazy<IOssService>(ossService);
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
            if (!await _ossService.Value.ObjectExistsAsync(key, timeout.Token)) throw new AudioStorageException();
            timeout.Token.ThrowIfCancellationRequested();
            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(timeProvider.GetUtcNow().ToUnixTimeSeconds());
            var url = await _ossService.Value.GetPresignedUrlAsync(key, Playback.LinkSeconds, issuedAt, timeout.Token);
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

    /// <summary>Stores one file and returns its object key.</summary>
    public async Task<string> PutAsync(Guid parseId, Guid audioId, string extension, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Status.Available) throw new AudioStorageException();
        string key;
        try
        {
            key = await _ossService.Value.UploadAsync(content, $"{audioId:N}{extension}", contentType, OssBucket.Audio, $"{parseId:N}", cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // SDK exceptions can carry endpoints, keys or signed URLs; only the type is logged.
            _logger.LogWarning("Audio object upload failed for audio {AudioId}: {ExceptionType}", audioId, ex.GetType().Name);
            throw new AudioStorageException();
        }

        if (key != $"{ParsePrefix(parseId)}{audioId:N}{extension}")
        {
            // An unexpected key may belong to another row. Only the service can confirm references.
            throw new InvalidOperationException("The storage returned an object key outside the audio prefix.");
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
    public async Task<List<OssObjectInfo>> ListAllAsync()
    {
        try
        {
            return (await _ossService.Value.ListObjectsOrThrowAsync(DocumentAudioOptions.ObjectPrefix))
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
            return await _ossService.Value.DeleteAsync(key);
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
