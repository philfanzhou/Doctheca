using System.Globalization;
using Doctheca.Domain.Models;
using Doctheca.Service.InternalApi;
using Xunit;

namespace Doctheca.Tests.Structure;

public sealed class DocumentAudioConfigurationTests
{
    [Fact]
    public void AbsentSettingsUseDefaultsAndPublicAddressOnlyControlsAdminPlayback()
    {
        var options = DocumentAudioOptions.Read(_ => null);
        Assert.Equal(20971520, options.MaxFileBytes);
        Assert.Equal(209715200, options.MaxArchiveBytes);
        Assert.Equal(300, options.PlaybackLinkSeconds);
        var storage = DocumentAudioStorageStatus.Evaluate(false, _ => "configured");
        Assert.True(storage.Available);
        Assert.True(InternalAudioSettings.Evaluate(storage, options.PlaybackLinkSeconds).Available);
        foreach (var address in new[] { null, "", "invalid", "https://user@example.invalid", "https://example.invalid/?x=1" })
            Assert.False(DocumentAudioPlaybackSettings.Evaluate(storage, options.PlaybackLinkSeconds, address).Available);
        Assert.True(DocumentAudioPlaybackSettings.Evaluate(storage, 300, "https://example.invalid/audio").Available);
    }

    [Theory]
    [InlineData("60", 60)]
    [InlineData("3600", 3600)]
    public void ValidLimitsHaveMultipartHeadroomAndOneTtl(string raw, int seconds)
    {
        var options = DocumentAudioOptions.Read(key => key switch
        {
            "DocumentAudio:MaxFileBytes" => "1",
            "DocumentAudio:MaxArchiveBytes" => (long.MaxValue - DocumentAudioOptions.MultipartOverheadBytes).ToString(CultureInfo.InvariantCulture),
            "DocumentAudio:PlaybackLinkSeconds" => raw,
            _ => null
        });
        Assert.Null(options.Validate());
        Assert.Equal(long.MaxValue, checked(options.MaxArchiveBytes + DocumentAudioOptions.MultipartOverheadBytes));
        var storage = new DocumentAudioStorageStatus(true, null);
        Assert.Equal(seconds, InternalAudioSettings.Evaluate(storage, options.PlaybackLinkSeconds).LinkSeconds);
        Assert.Equal(seconds, DocumentAudioPlaybackSettings.Evaluate(storage, options.PlaybackLinkSeconds, "https://example.invalid").LinkSeconds);
    }

    public static IEnumerable<object[]> InvalidSettings()
    {
        foreach (var key in new[] { "MaxFileBytes", "MaxArchiveBytes", "PlaybackLinkSeconds" })
        foreach (var value in new[] { "", "configuration-canary", "1.5", "9223372036854775808" })
            yield return new object[] { "DocumentAudio:" + key, value };
        foreach (var value in new[] { "0", "-1" }) yield return new object[] { "DocumentAudio:MaxFileBytes", value };
        foreach (var value in new[] { "1", "9223372036854775807" }) yield return new object[] { "DocumentAudio:MaxArchiveBytes", value };
        foreach (var value in new[] { "59", "3601", "abc" }) yield return new object[] { "DocumentAudio:PlaybackLinkSeconds", value };
    }

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void InvalidSettingsReportOnlyKeyNames(string key, string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => DocumentAudioOptions.Read(name => name == key ? value : null));
        Assert.Contains(key, error.Message);
        Assert.DoesNotContain("configuration-canary", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("InternalEndpoint")]
    [InlineData("AccessKey")]
    [InlineData("SecretKey")]
    [InlineData("BucketName")]
    public void EachMissingS3KeyDisablesStorageWithKeyOnlyReason(string missing)
    {
        var status = DocumentAudioStorageStatus.Evaluate(false, key => key == missing ? " " : "storage-value-canary");
        Assert.False(status.Available);
        Assert.Equal("missing Oss:" + missing, status.Reason);
        Assert.False(InternalAudioSettings.Evaluate(status, 300).Available);
        Assert.False(DocumentAudioPlaybackSettings.Evaluate(status, 300, "https://example.invalid").Available);
    }

    [Fact]
    public void LocalFileNeverEnablesAudio()
    {
        var status = DocumentAudioStorageStatus.Evaluate(true, _ => "storage-value-canary");
        Assert.False(status.Available);
        Assert.Contains("USE_LOCAL_OSS", status.Reason);
        Assert.DoesNotContain("storage-value-canary", status.Reason);
    }
}
