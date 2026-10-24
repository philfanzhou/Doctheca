using System.Globalization;

namespace Doctheca.Domain.Models;

/// <summary>Startup snapshot of audio import and object lifecycle settings.</summary>
public sealed class DocumentAudioOptions
{
    public const string SectionName = "DocumentAudio";
    public const string ObjectPrefix = Doctheca.Common.Constants.OssPathPrefixConstants.Audio;
    public const int MaxFilesPerImport = 1000;
    public const int MaxArchiveEntries = 4000;
    public const long MultipartOverheadBytes = 1024 * 1024;
    public long MaxFileBytes { get; set; } = 20L * 1024 * 1024;
    public long MaxArchiveBytes { get; set; } = 200L * 1024 * 1024;

    public int PlaybackLinkSeconds { get; set; } = 300;

    public static DocumentAudioOptions Read(Func<string, string?> setting)
    {
        var options = new DocumentAudioOptions();
        options.MaxFileBytes = ReadInteger("MaxFileBytes", options.MaxFileBytes);
        options.MaxArchiveBytes = ReadInteger("MaxArchiveBytes", options.MaxArchiveBytes);
        var seconds = ReadInteger("PlaybackLinkSeconds", options.PlaybackLinkSeconds);
        if (seconds is < 60 or > 3600) throw Invalid("PlaybackLinkSeconds");
        options.PlaybackLinkSeconds = (int)seconds;
        if (options.Validate() is { } error) throw new InvalidOperationException(error);
        return options;

        long ReadInteger(string key, long fallback)
        {
            var raw = setting($"{SectionName}:{key}");
            if (raw is null) return fallback;
            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw Invalid(key);
            return value;
        }
    }

    private static InvalidOperationException Invalid(string key) =>
        new($"DocumentAudio:{key} must be a valid integer within its allowed range");

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
    public static DocumentAudioStorageStatus Evaluate(bool useLocalOss,
        Func<string, string?> ossSetting)
    {
        if (useLocalOss) return new(false, "audio objects require S3 storage (USE_LOCAL_OSS is enabled)");
        var missing = new[] { "InternalEndpoint", "AccessKey", "SecretKey", "BucketName" }
            .Where(key => string.IsNullOrWhiteSpace(ossSetting(key))).Select(key => "Oss:" + key).ToList();
        return missing.Count > 0 ? new(false, "missing " + string.Join(", ", missing)) : new(true, null);
    }
}

/// <summary>Playback availability is independent of import and cleanup configuration.</summary>
public sealed record DocumentAudioPlaybackSettings(bool Available, int LinkSeconds)
{
    public static DocumentAudioPlaybackSettings Evaluate(DocumentAudioStorageStatus storage, int seconds, string? publicBaseUrl)
    {
        var validPublic = Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            && string.IsNullOrEmpty(uri.UserInfo);
        return new(storage.Available && validPublic, seconds);
    }
}
