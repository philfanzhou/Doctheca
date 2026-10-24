using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Doctheca.Common.Oss;
using Doctheca.Domain.Models;
using Doctheca.Service.Audio;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class StructureAudioEndpointTests
{
    [Fact]
    public async Task NoAudioSection_AllowsImportReviewMetadataAndInternalBytePlayback()
    {
        var root = CreateTempContentRoot(true);
        try
        {
            using var factory = CreateFactory(contentRoot: root, settings: minio.Settings(_bucket), configureTestServices: services =>
            {
                services.RemoveAll<IOssService>();
                services.AddScoped<IOssService>(sp => new S3OssService(sp.GetRequiredService<IOptions<OssOptions>>().Value));
            });
            using var client = Admin(factory);
            var (_, parseId) = await SeedTextbookAsync(factory, client);
            var wave = PlayableWave.Bytes();
            var imported = await ReadData(await client.PostAsync($"/admin/document-parses/{parseId}/audio", Upload(("sample.wav", wave))));
            var id = imported!["items"]![0]!["id"]!.GetValue<string>();
            var tree = await ReadData(await client.GetAsync($"/admin/document-parses/{parseId}/structure"));
            var node = tree!["nodes"]![0]!["id"]!.GetValue<string>();
            await ReadData(await client.PutAsJsonAsync($"/admin/document-parses/{parseId}/audio", new { links = new[] { new { id, nodeId = node } }, removeIds = Array.Empty<string>() }));
            var reloaded = await ReadData(await client.GetAsync($"/admin/document-parses/{parseId}/audio"));
            reloaded!["storage"]!["available"]!.GetValue<bool>().Should().BeTrue();
            reloaded["storage"]!["maxFileBytes"]!.GetValue<long>().Should().Be(20971520);
            reloaded["items"]![0]!["nodeId"]!.GetValue<string>().Should().Be(node);
            (await ReadData(await client.GetAsync($"/admin/document-parses/{parseId}/structure/nodes/{node}/audio")))!["items"]!.AsArray().Should().ContainSingle();
            client.DefaultRequestHeaders.Authorization = null;
            var list = await ReadData(await client.GetAsync($"/internal/v1/structure-nodes/{node}/audio"));
            var link = list!["items"]![0]!["playback"]!["url"]!.GetValue<string>();
            (await client.GetByteArrayAsync(link)).Should().Equal(wave);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task NodeAudioListsOnlyDirectLinksAndChecksOwnership()
    {
        using var factory = Factory();
        using var client = Admin(factory);
        var (_, parseId) = await SeedTextbookAsync(factory, client);
        var imported = await ReadData(await client.PostAsync($"/admin/document-parses/{parseId}/audio",
            Upload(("unit1/VocabularyUnit1.wav", PlayableWave.Bytes()), ("unit1/Unit1SectionA.wav", PlayableWave.Bytes()), ("unlinked.wav", PlayableWave.Bytes()))));
        var nodes = (await ReadData(await client.GetAsync($"/admin/document-parses/{parseId}/structure")))!["nodes"]!;
        var root = nodes[0]!["id"]!.GetValue<string>();
        var child = nodes[0]!["children"]![0]!["id"]!.GetValue<string>();
        foreach (var (node, expected) in new[] { (root, "unit1/VocabularyUnit1.wav"), (child, "unit1/Unit1SectionA.wav") })
        {
            var response = await client.GetAsync($"/admin/document-parses/{parseId}/structure/nodes/{node}/audio");
            var data = await ReadData(response);
            AssertNoStorageDetails(data!.ToJsonString());
            data["items"]!.AsArray().Should().ContainSingle();
            data["items"]![0]!["sourcePath"]!.GetValue<string>().Should().Be(expected);
        }
        var (_, other) = await SeedTextbookAsync(factory, client);
        foreach (var path in new[] {
            $"{Guid.NewGuid()}/structure/nodes/{root}/audio", $"{other}/structure/nodes/{root}/audio",
            $"{parseId}/structure/nodes/{Guid.NewGuid()}/audio", $"{other}/audio/{imported!["items"]![0]!["id"]}/playback-link",
            $"{Guid.NewGuid()}/audio/{Guid.NewGuid()}/playback-link", $"{parseId}/audio/{Guid.NewGuid()}/playback-link" })
        {
            var response = await client.GetAsync("/admin/document-parses/" + path);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            AssertNoStorageDetails(await response.Content.ReadAsStringAsync());
            if (path.EndsWith("playback-link")) response.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
        // Unlinked rows can be previewed by an administrator, too.
        var unlinked = imported!["items"]!.AsArray().Single(item => item!["nodeId"] == null)!;
        (await client.GetAsync($"/admin/document-parses/{parseId}/audio/{unlinked["id"]}/playback-link")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PlaybackDownloadsRealWaveAndExpiresAtTheS3Boundary()
    {
        var logs = new RequestScopeCapture();
        using var factory = Factory(extra: new() { ["DocumentAudio:PlaybackLinkSeconds"] = "60" }, logs: logs);
        using var client = Admin(factory);
        var (_, parseId) = await SeedTextbookAsync(factory, client);
        var wave = PlayableWave.Bytes();
        var item = (await ReadData(await client.PostAsync($"/admin/document-parses/{parseId}/audio", Upload(("unit1/SectionA.wav", wave)))))!["items"]![0]!;
        var before = DateTimeOffset.UtcNow;
        var response = await client.GetAsync($"/admin/document-parses/{parseId}/audio/{item["id"]}/playback-link");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var data = (await ReadData(response))!.AsObject();
        data.Select(pair => pair.Key).Should().BeEquivalentTo("url", "expiresAt", "expiresInSeconds");
        data["expiresInSeconds"]!.GetValue<int>().Should().Be(60);
        var expiry = data["expiresAt"]!.GetValue<DateTimeOffset>();
        expiry.Should().BeCloseTo(before.AddSeconds(60), TimeSpan.FromSeconds(2));
        var url = data["url"]!.GetValue<string>();
        var uri = new Uri(url);
        Assert.True(uri.Host == "localhost", "Playback did not use the public host.");
        Assert.True(!url.Contains(minio.InternalEndpoint) && !url.Contains(minio.SecretKey)
            && !url.Contains(CreateToken("admin")), "Playback leaked internal or application credentials.");
        using var download = new HttpClient();
        using var bytes = await download.GetAsync(url);
        Assert.True(bytes.StatusCode == HttpStatusCode.OK, "Public playback bytes unavailable before expiry.");
        (await bytes.Content.ReadAsByteArrayAsync()).Should().Equal(wave);
        // The existing non-audio presigning method still signs with its original client behavior.
        using (var scope = factory.Services.CreateScope())
        {
            var legacy = await scope.ServiceProvider.GetRequiredService<IOssService>()
                .GetPresignedUrlAsync((await minio.KeysAsync(_bucket)).Single(), 60);
            using var legacyBytes = await download.GetAsync(legacy);
            Assert.True(legacyBytes.StatusCode == HttpStatusCode.OK, "Existing S3 presigning behavior changed.");
        }
        using (var disabled = Factory(extra: new() { ["Oss:PublicBaseUrl"] = "" }))
        using (var disabledClient = Admin(disabled))
        {
            await AssertUnavailable(await disabledClient.GetAsync($"/admin/document-parses/{parseId}/audio/{item["id"]}/playback-link"));
            using var issued = await download.GetAsync(url);
            Assert.True(issued.StatusCode == HttpStatusCode.OK, "Removing the public address unexpectedly revoked an issued S3 credential.");
        }
        var wait = expiry - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        using var expired = await download.GetAsync(url);
        Assert.True(expired.StatusCode == HttpStatusCode.Forbidden, "S3 accepted an expired playback credential.");
        var captured = string.Join('\n', logs.Logs.Select(log => log.Message));
        Assert.True(!captured.Contains(url) && !captured.Contains("X-Amz-Signature")
            && !captured.Contains(minio.SecretKey) && !captured.Contains(client.DefaultRequestHeaders.Authorization!.Parameter!)
            && !captured.Contains(minio.InternalEndpoint) && !captured.Contains(_bucket) && !captured.Contains(minio.AccessKey), "Logs leaked playback or application credentials.");
        (await CountAudioAsync(factory, parseId)).Should().Be(1);
    }

    [Theory]
    [InlineData("Oss:PublicBaseUrl", "")]
    [InlineData("Oss:PublicBaseUrl", "not-a-url")]
    [InlineData("Oss:PublicBaseUrl", "https://user@example.invalid/path")]
    [InlineData("Oss:PublicBaseUrl", "https://example.invalid/path?token=x")]
    public async Task InvalidPlaybackSettingsLeaveImportAvailable(string key, string value)
    {
        using var factory = Factory(extra: new() { [key] = value });
        using var client = Admin(factory);
        var (_, parseId) = await SeedTextbookAsync(factory, client);
        var item = (await ReadData(await client.PostAsync($"/admin/document-parses/{parseId}/audio", Upload(("unit1/SectionA.wav", PlayableWave.Bytes())))))!["items"]![0]!;
        var response = await client.GetAsync($"/admin/document-parses/{parseId}/audio/{item["id"]}/playback-link");
        await AssertUnavailable(response);
        (await client.GetAsync("/admin/document-files")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadData(await client.GetAsync($"/admin/document-parses/{parseId}/audio")))!["storage"]!["available"]!.GetValue<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData(null, 300)]
    [InlineData("3600", 3600)]
    public async Task PlaybackDefaultsAndPublicPrefixUseSeparateClientsWithoutPublicDiscovery(string? configuredSeconds, int seconds)
    {
        var settings = new Dictionary<string, string?> { ["Oss:PublicBaseUrl"] = "https://audio.example.invalid/oss/proxy" };
        if (configuredSeconds != null) settings["DocumentAudio:PlaybackLinkSeconds"] = configuredSeconds;
        using var factory = Factory(extra: settings);
        using var client = Admin(factory);
        var (_, parseId) = await SeedTextbookAsync(factory, client);
        var item = (await ReadData(await client.PostAsync($"/admin/document-parses/{parseId}/audio", Upload(("Unit1.wav", PlayableWave.Bytes())))))!["items"]![0]!;
        var data = await ReadData(await client.GetAsync($"/admin/document-parses/{parseId}/audio/{item["id"]}/playback-link"));
        data!["expiresInSeconds"]!.GetValue<int>().Should().Be(seconds);
        var uri = new Uri(data["url"]!.GetValue<string>());
        Assert.True(uri.Host == "audio.example.invalid" && uri.Scheme == "https"
            && uri.AbsolutePath.StartsWith("/oss/proxy/" + _bucket + "/audio/", StringComparison.Ordinal), "Public signing prefix was not preserved.");
        // A failed internal Stat must refuse signing even though the public signer itself needs no network.
        using (var scope = factory.Services.CreateScope())
        {
            var oss = scope.ServiceProvider.GetRequiredService<IOssService>();
            await oss.DeleteAsync((await minio.KeysAsync(_bucket)).Single());
        }
        await AssertUnavailable(await client.GetAsync($"/admin/document-parses/{parseId}/audio/{item["id"]}/playback-link"));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("denied")]
    [InlineData("unreachable")]
    public async Task UnusableStorageProducesOnlyFixedSafePlaybackFailure(string failure)
    {
        using var factory = Factory();
        using var client = Admin(factory);
        var (_, parseId) = await SeedTextbookAsync(factory, client);
        var item = (await ReadData(await client.PostAsync($"/admin/document-parses/{parseId}/audio", Upload(("Unit1.wav", PlayableWave.Bytes())))))!["items"]![0]!;
        var settings = failure switch {
            "disabled" => new Dictionary<string, string?> { ["Oss:PublicBaseUrl"] = "" },
            "denied" => new Dictionary<string, string?> { ["Oss:AccessKey"] = "missing-synthetic-key" },
            _ => new Dictionary<string, string?> { ["Oss:InternalEndpoint"] = "127.0.0.1:1" },
        };
        using var unavailable = Factory(extra: settings);
        using var unavailableClient = Admin(unavailable);
        await AssertUnavailable(await unavailableClient.GetAsync($"/admin/document-parses/{parseId}/audio/{item["id"]}/playback-link"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaybackCancellationBeforeAndDuringStorageNeverReturnsALink(bool wrappedSdkFailure)
    {
        var oss = new Mock<IOssService>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        var key = $"audio/{Guid.NewGuid():N}/{Guid.NewGuid():N}.wav";
        var store = new StructureAudioStore(() => oss.Object, new(true, null), NullLogger<StructureAudioStore>.Instance, new(true, 60));
        oss.Setup(o => o.ObjectExistsAsync(key, It.IsAny<CancellationToken>())).Returns((string _, CancellationToken token) => {
            cancellation.Cancel();
            if (wrappedSdkFailure) throw new IOException("synthetic-cancelled-provider-detail");
            token.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetPlaybackLinkAsync(key, TimeProvider.System, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetPlaybackLinkAsync(key, TimeProvider.System, cancellation.Token));
        oss.Verify(o => o.ObjectExistsAsync(key, It.IsAny<CancellationToken>()), Times.Once);
        oss.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SigningFailureDiscardsProviderDiagnosticsAndCancelledSigningReturnsNoSuccess()
    {
        var oss = new Mock<IOssService>(MockBehavior.Strict);
        var key = $"audio/{Guid.NewGuid():N}/{Guid.NewGuid():N}.wav";
        var logs = new RequestScopeCapture();
        using var loggerFactory = new LoggerFactory([logs]);
        var store = new StructureAudioStore(() => oss.Object, new(true, null),
            loggerFactory.CreateLogger<StructureAudioStore>(), new(true, 60));
        oss.Setup(o => o.ObjectExistsAsync(key, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        oss.Setup(o => o.GetPresignedUrlAsync(key, 60, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("synthetic-private-provider-detail"));
        var error = await Assert.ThrowsAsync<AudioStorageException>(() => store.GetPlaybackLinkAsync(key, TimeProvider.System, CancellationToken.None));
        Assert.DoesNotContain("synthetic-private-provider-detail", error.ToString());
        Assert.All(logs.Logs, log => Assert.DoesNotContain("synthetic-private-provider-detail", log.Message));
        using var cancellation = new CancellationTokenSource();
        oss.Setup(o => o.GetPresignedUrlAsync(key, 60, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns((string _, int seconds, DateTimeOffset issued, CancellationToken token) => {
                cancellation.Cancel();
                return Task.FromResult("unused-synthetic-result");
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetPlaybackLinkAsync(key, TimeProvider.System, cancellation.Token));
    }

    private async Task AssertUnavailable(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await response.Content.ReadAsStringAsync();
        AssertNoStorageDetails(body);
        var data = JsonNode.Parse(body)!;
        data["message"]!.GetValue<string>().Should().Be("Audio storage is unavailable");
        data["errorCode"]!.GetValue<string>().Should().Be(AudioErrorCodes.StorageUnavailable);
    }
}

/// <summary>Three seconds of valid PCM WAV; useful for actual browser media decoding.</summary>
internal static class PlayableWave
{
    internal static byte[] Bytes()
    {
        const int samples = 8000 * 3;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000);
        writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++) writer.Write((short)(1000 * Math.Sin(2 * Math.PI * 220 * i / 8000)));
        return stream.ToArray();
    }
}
