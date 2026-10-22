namespace Doctheca.Domain.Models;

/// <summary>Default-off audio object lifecycle settings (#107); import/playback are separate tasks.</summary>
public sealed class DocumentAudioOptions
{
    public const string SectionName = "DocumentAudio";
    public const string ObjectPrefix = Doctheca.Common.Constants.OssPathPrefixConstants.Audio;
    public bool Enabled { get; set; }
}

/// <summary>Whether the S3 audio lifecycle can be used; reasons contain key names only.</summary>
public sealed record DocumentAudioStorageStatus(bool Available, string? Reason)
{
    public static DocumentAudioStorageStatus Evaluate(DocumentAudioOptions options, bool useLocalOss,
        Func<string, string?> ossSetting)
    {
        if (!options.Enabled) return new(false, "DocumentAudio:Enabled is not true");
        if (useLocalOss) return new(false, "audio objects require S3 storage (USE_LOCAL_OSS is enabled)");
        var missing = new[] { "InternalEndpoint", "AccessKey", "SecretKey", "BucketName" }
            .Where(key => string.IsNullOrWhiteSpace(ossSetting(key))).Select(key => "Oss:" + key).ToList();
        return missing.Count > 0 ? new(false, "missing " + string.Join(", ", missing)) : new(true, null);
    }
}
