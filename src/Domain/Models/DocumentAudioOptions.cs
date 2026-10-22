namespace Doctheca.Domain.Models;

/// <summary>Default-off audio import and object lifecycle settings.</summary>
public sealed class DocumentAudioOptions
{
    public const string SectionName = "DocumentAudio";
    public const string ObjectPrefix = Doctheca.Common.Constants.OssPathPrefixConstants.Audio;
    public const int MaxFilesPerImport = 1000;
    public const int MaxArchiveEntries = 4000;
    public const long MultipartOverheadBytes = 1024 * 1024;
    public bool Enabled { get; set; }
    public long MaxFileBytes { get; set; } = 20L * 1024 * 1024;
    public long MaxArchiveBytes { get; set; } = 200L * 1024 * 1024;

    public string? Validate()
    {
        if (MaxFileBytes < 1) return "DocumentAudio:MaxFileBytes must be positive";
        if (MaxArchiveBytes < MaxFileBytes) return "DocumentAudio:MaxArchiveBytes must be at least MaxFileBytes";
        if (MaxArchiveBytes > long.MaxValue - MultipartOverheadBytes)
            return "DocumentAudio:MaxArchiveBytes leaves no room for multipart overhead";
        return null;
    }
}

/// <summary>Whether the S3 audio lifecycle can be used; reasons contain key names only.</summary>
public sealed record DocumentAudioStorageStatus(bool Available, string? Reason)
{
    public static DocumentAudioStorageStatus Evaluate(DocumentAudioOptions options, bool useLocalOss,
        Func<string, string?> ossSetting)
    {
        if (!options.Enabled) return new(false, "DocumentAudio:Enabled is not true");
        if (options.Validate() is { } invalid) return new(false, invalid);
        if (useLocalOss) return new(false, "audio objects require S3 storage (USE_LOCAL_OSS is enabled)");
        var missing = new[] { "InternalEndpoint", "AccessKey", "SecretKey", "BucketName" }
            .Where(key => string.IsNullOrWhiteSpace(ossSetting(key))).Select(key => "Oss:" + key).ToList();
        return missing.Count > 0 ? new(false, "missing " + string.Join(", ", missing)) : new(true, null);
    }
}

/// <summary>Playback availability is independent of import and cleanup configuration.</summary>
public sealed record DocumentAudioPlaybackSettings(bool Available, int LinkSeconds)
{
    public static DocumentAudioPlaybackSettings Evaluate(DocumentAudioStorageStatus storage, string? seconds, string? publicBaseUrl)
    {
        var ttl = 300;
        if (seconds != null && !int.TryParse(seconds, out ttl)) return new(false, 300);
        var validPublic = Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            && string.IsNullOrEmpty(uri.UserInfo);
        return new(storage.Available && ttl is >= 60 and <= 3600 && validPublic, ttl);
    }
}
