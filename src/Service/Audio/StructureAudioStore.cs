using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Doctheca.Common.Oss;
using Doctheca.Domain.Models;

namespace Doctheca.Service.Audio;

/// <summary>Raised for any object storage failure; carries no SDK message, endpoint or key.</summary>
public sealed class AudioStorageException() : Exception("Audio storage is unavailable.");

/// <summary>
/// Structure audio objects in the existing S3-compatible storage (<see cref="IOssService"/>),
/// always under <see cref="DocumentAudioOptions.ObjectPrefix"/> as
/// <c>audio/{parseId}/{audioId}{extension}</c>. Keys never leave the server; playback uses
/// short-lived presigned links.
/// </summary>
public sealed class StructureAudioStore
{
    private readonly IOssService _ossService;
    private readonly DocumentAudioOptions _options;
    private readonly ILogger<StructureAudioStore> _logger;

    public StructureAudioStore(
        IOssService ossService,
        IOptions<DocumentAudioOptions> options,
        DocumentAudioStorageStatus status,
        ILogger<StructureAudioStore> logger)
    {
        _ossService = ossService;
        _options = options.Value;
        Status = status;
        _logger = logger;
    }

    public DocumentAudioStorageStatus Status { get; }

    internal static string ParsePrefix(Guid parseId) => $"{DocumentAudioOptions.ObjectPrefix}{parseId:N}/";

    /// <summary>Stores one file and returns its object key.</summary>
    public async Task<string> PutAsync(Guid parseId, Guid audioId, string extension, Stream content, string contentType)
    {
        string key;
        try
        {
            key = await _ossService.UploadAsync(content, $"{audioId:N}{extension}", contentType, OssBucket.Audio, $"{parseId:N}");
        }
        catch (Exception ex)
        {
            // SDK exceptions can carry endpoints, keys or signed URLs; only the type is logged.
            _logger.LogWarning("Audio object upload failed for audio {AudioId}: {ExceptionType}", audioId, ex.GetType().Name);
            throw new AudioStorageException();
        }

        if (!key.StartsWith(ParsePrefix(parseId), StringComparison.Ordinal))
        {
            await DeleteAsync(key);
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

    /// <summary>
    /// Deletes every object under the parse's prefix, including any left by an interrupted import;
    /// returns how many could not be deleted.
    /// </summary>
    public async Task<int> DeleteParseAsync(Guid parseId, IEnumerable<string> knownKeys)
    {
        var keys = new HashSet<string>(knownKeys, StringComparer.Ordinal);
        try
        {
            foreach (var item in await _ossService.ListObjectsAsync(ParsePrefix(parseId)))
                keys.Add(item.ObjectPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Listing audio objects of parse {ParseId} failed: {ExceptionType}", parseId, ex.GetType().Name);
        }
        return await DeleteManyAsync(keys);
    }

    /// <summary>Every stored audio object, for the orphan sweep.</summary>
    public async Task<List<OssObjectInfo>> ListAllAsync()
    {
        try
        {
            return (await _ossService.ListObjectsAsync(DocumentAudioOptions.ObjectPrefix))
                .Where(item => item.ObjectPath.StartsWith(DocumentAudioOptions.ObjectPrefix, StringComparison.Ordinal))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Listing audio objects failed: {ExceptionType}", ex.GetType().Name);
            throw new AudioStorageException();
        }
    }

    /// <summary>A presigned GET link valid for <see cref="DocumentAudioOptions.PlaybackLinkSeconds"/>.</summary>
    public async Task<(string Url, int ExpiresInSeconds)> GetPlaybackLinkAsync(string key)
    {
        EnsureAudioKey(key);
        try
        {
            return (await _ossService.GetPresignedUrlAsync(key, _options.PlaybackLinkSeconds), _options.PlaybackLinkSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Signing an audio playback link failed: {ExceptionType}", ex.GetType().Name);
            throw new AudioStorageException();
        }
    }

    private async Task<bool> DeleteAsync(string key)
    {
        EnsureAudioKey(key);
        try
        {
            return await _ossService.DeleteAsync(key);
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
