namespace Doctheca.Domain.Models;

/// <summary>
/// Structure audio settings (configuration section <c>DocumentAudio</c>, issue #90). Audio is
/// stored through the existing <c>Oss</c> S3-compatible storage under the
/// <see cref="ObjectPrefix"/> prefix; it is off until <see cref="Enabled"/> is true.
/// </summary>
public sealed class DocumentAudioOptions
{
    public const string SectionName = "DocumentAudio";

    /// <summary>The object key prefix of every audio object; a code constant, not configuration.</summary>
    public const string ObjectPrefix = Doctheca.Common.Constants.OssPathPrefixConstants.Audio;

    /// <summary>The most files one import may contain, whether uploaded or inside archives.</summary>
    public const int MaxFilesPerImport = 1000;

    public const int MinPlaybackLinkSeconds = 60;
    public const int MaxPlaybackLinkSeconds = 3600;

    public bool Enabled { get; set; }

    /// <summary>The largest accepted audio file (an upload or an archive entry). Default 20 MiB.</summary>
    public long MaxFileBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>
    /// The largest import: the uploaded bytes of one request (a ZIP or several files) and the
    /// total uncompressed audio of an import. Default 200 MiB.
    /// </summary>
    public long MaxArchiveBytes { get; set; } = 200L * 1024 * 1024;

    /// <summary>How long a playback link stays valid, 60–3600 seconds. Default 300.</summary>
    public int PlaybackLinkSeconds { get; set; } = 300;

    /// <summary>The reason the settings are unusable, or null when they are valid.</summary>
    public string? Validate()
    {
        if (MaxFileBytes < 1) return "DocumentAudio:MaxFileBytes must be positive";
        if (MaxArchiveBytes < MaxFileBytes) return "DocumentAudio:MaxArchiveBytes must be at least MaxFileBytes";
        if (PlaybackLinkSeconds is < MinPlaybackLinkSeconds or > MaxPlaybackLinkSeconds)
            return $"DocumentAudio:PlaybackLinkSeconds must be between {MinPlaybackLinkSeconds} and {MaxPlaybackLinkSeconds}";
        return null;
    }
}

/// <summary>Whether audio storage can be used in this process, decided once at startup.</summary>
public sealed record DocumentAudioStorageStatus(bool Available, string? Reason)
{
    /// <summary>
    /// Audio needs <c>DocumentAudio:Enabled</c>, valid limits, S3 storage (the local-file store
    /// cannot sign links), and every <c>Oss</c> value a signed public link depends on.
    /// </summary>
    public static DocumentAudioStorageStatus Evaluate(
        DocumentAudioOptions options,
        bool useLocalOss,
        Func<string, string?> ossSetting)
    {
        if (!options.Enabled) return new(false, "DocumentAudio:Enabled is not true");
        if (options.Validate() is { } invalid) return new(false, invalid);
        if (useLocalOss) return new(false, "the local file store (USE_LOCAL_OSS) cannot sign playback links");
        var missing = new[] { "InternalEndpoint", "AccessKey", "SecretKey", "BucketName", "PublicBaseUrl" }
            .Where(key => string.IsNullOrWhiteSpace(ossSetting(key)))
            .Select(key => "Oss:" + key)
            .ToList();
        return missing.Count > 0
            ? new(false, "missing " + string.Join(", ", missing))
            : new(true, null);
    }
}
