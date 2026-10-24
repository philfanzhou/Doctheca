using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Doctheca.Common.Oss;
using Doctheca.Database;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Service;
using Doctheca.Service.Audio;
using Doctheca.Service.InternalApi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class InternalAudioEndpointTests
{
    [Theory]
    [InlineData(null)][InlineData("false")][InlineData("invalid")]
    public async Task ObsoleteEnableValues_DoNotDisableAudioRoutes(string? enabled)
    {
        var repository = new Mock<IInternalAudioRepository>(MockBehavior.Strict);
        var oss = new Mock<IOssService>(MockBehavior.Strict);
        var node = Guid.NewGuid();
        repository.Setup(r => r.ReadAudioAsync(node, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InternalAudioMembership?)null);
        using var factory = Factory(services =>
        { services.RemoveAll<IInternalAudioRepository>(); services.AddSingleton(repository.Object); services.RemoveAll<IOssService>(); services.AddSingleton(oss.Object); }, new() { ["InternalApi:Enabled"] = enabled });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(ListUrl(node));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Headers(response);
        Assert.Contains("DOCTHECA_INTERNAL_NODE_NOT_FOUND", await response.Content.ReadAsStringAsync());
        repository.Verify(r => r.ReadAudioAsync(node, null, It.IsAny<CancellationToken>()), Times.Once);
        repository.VerifyNoOtherCalls(); oss.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LocalFileMode_DisablesProxyWithoutStorageConstruction()
    {
        var old = Environment.GetEnvironmentVariable("USE_LOCAL_OSS"); var oldPath = Environment.GetEnvironmentVariable("OSS_LOCAL_PATH");
        var directory = Path.Combine(Path.GetTempPath(), "doctheca-local-audio-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("USE_LOCAL_OSS", "1"); Environment.SetEnvironmentVariable("OSS_LOCAL_PATH", directory);
            var oss = new Mock<IOssService>(MockBehavior.Strict);
            using var factory = Factory(services => { services.RemoveAll<IOssService>(); services.AddSingleton(oss.Object); });
            using var client = factory.CreateClient(); var seed = await SeedAsync(factory.Services, PlayableWave.Bytes());
            try
            {
                using var response = await client.GetAsync(ListUrl(seed.Node));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                var token = factory.Services.GetRequiredService<InternalAudioTokens>().Create(
                    new(seed.File, seed.Tree, seed.Node, seed.Audio), DateTimeOffset.UtcNow.AddMinutes(2));
                using var bytes = await client.GetAsync(ListUrl(seed.Node) + $"/{seed.Audio:D}/content?token=" + token);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, bytes.StatusCode);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin"));
                using var import = await client.PostAsync($"/admin/document-parses/{seed.Parse}/audio", null);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, import.StatusCode);
                Assert.Contains("USE_LOCAL_OSS", await import.Content.ReadAsStringAsync());
                using var sweep = await client.PostAsync("/admin/structure-audio/orphan-sweep", null);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, sweep.StatusCode);
                using var list = await client.GetAsync($"/admin/document-parses/{seed.Parse}/audio");
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
                using var direct = await client.GetAsync($"/admin/document-parses/{seed.Parse}/structure/nodes/{seed.Node}/audio");
                Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
                using var review = await client.PutAsJsonAsync($"/admin/document-parses/{seed.Parse}/audio",
                    new { links = new[] { new { id = seed.Audio, nodeId = seed.Node } } });
                Assert.Equal(HttpStatusCode.OK, review.StatusCode);
                using var adminPlayback = await client.GetAsync($"/admin/document-parses/{seed.Parse}/audio/{seed.Audio}/playback-link");
                Assert.Equal(HttpStatusCode.ServiceUnavailable, adminPlayback.StatusCode);
                using var missing = await client.PostAsync($"/admin/document-parses/{Guid.NewGuid()}/audio", null);
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
                oss.VerifyNoOtherCalls();
            }
            finally { await Cleanup(factory.Services, seed); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("USE_LOCAL_OSS", old); Environment.SetEnvironmentVariable("OSS_LOCAL_PATH", oldPath);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("missing")][InlineData("denied")][InlineData("unreachable")]
    public async Task RealS3Failures_ProduceFixed503AndNoStorageDisclosure(string failure)
    {
        var settings = new Dictionary<string, string?>();
        if (failure == "denied") settings["Oss:SecretKey"] = "synthetic-invalid-secret";
        if (failure == "unreachable") settings["Oss:InternalEndpoint"] = "127.0.0.1:1";
        var logs = new Capture(); using var factory = Factory(services => services.AddSingleton<ILoggerProvider>(logs), settings);
        using var client = factory.CreateClient(); var seed = await SeedAsync(factory.Services, PlayableWave.Bytes());
        try
        {
            var link = await Link(client, seed.Node); using var response = await client.GetAsync(link); Headers(response);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Safe(await response.Content.ReadAsStringAsync() + response.Headers + response.Content.Headers + string.Join('\n', logs.Messages), _bucket, minio.InternalEndpoint, minio.AccessKey, minio.SecretKey, Key(seed.Parse, seed.Audio), "synthetic-invalid-secret", link);
        }
        finally { await Cleanup(factory.Services, seed); }
    }

    [Fact]
    public async Task OpaqueTokens_ExactExpiryMaximumTtlAndPublicUrlIndependence()
    {
        var provider = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        var tokens = new InternalAudioTokens(provider, TimeProvider.System);
        using var factory = Factory(services => services.AddSingleton(tokens), new() { ["DocumentAudio:PlaybackLinkSeconds"] = "3600", ["Oss:PublicBaseUrl"] = "" });
        using var client = factory.CreateClient(); var seed = await SeedAsync(factory.Services, PlayableWave.Bytes());
        try
        {
            using var json = JsonDocument.Parse(await client.GetStringAsync(ListUrl(seed.Node)));
            var playback = json.RootElement.GetProperty("data").GetProperty("items")[0].GetProperty("playback");
            var link = playback.GetProperty("url").GetString()!;
            var token = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri("http://test" + link).Query)["token"].ToString();
            var protector = Microsoft.AspNetCore.DataProtection.DataProtectionCommonExtensions.CreateProtector(provider, InternalAudioTokens.Purpose);
            var timed = Microsoft.AspNetCore.DataProtection.DataProtectionAdvancedExtensions.ToTimeLimitedDataProtector(protector);
            var bytes = timed.Unprotect(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(token), out var expiry);
            Assert.Equal(playback.GetProperty("expiresAt").GetDateTimeOffset(), expiry); Assert.Equal(3600, playback.GetProperty("expiresInSeconds").GetInt32());
            Assert.Equal(65, bytes.Length); Assert.Equal(seed.Tree, new Guid(bytes.AsSpan(17, 16))); Assert.Equal(seed.Node, new Guid(bytes.AsSpan(33, 16))); Assert.Equal(seed.Audio, new Guid(bytes.AsSpan(49, 16)));
            var context = await factory.Server.SendAsync(c => { c.Request.Method = "GET"; c.Request.PathBase = "/mounted"; c.Request.Path = ListUrl(seed.Node); });
            using var reader = new StreamReader(context.Response.Body); using var mounted = JsonDocument.Parse(await reader.ReadToEndAsync());
            var sameBase = mounted.RootElement.GetProperty("data").GetProperty("items")[0].GetProperty("playback").GetProperty("url").GetString()!.StartsWith("/mounted/internal/", StringComparison.Ordinal);
            Assert.True(sameBase, "PathBase was not retained.");
        }
        finally { await Cleanup(factory.Services, seed); }
    }

    [Fact]
    public async Task SourceOrderingAndCrossParseRows_AreIndependentOfDatabaseCollation()
    {
        using var factory = Factory(); using var client = factory.CreateClient(); var seed = await SeedAsync(factory.Services, PlayableWave.Bytes());
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
                db.DocumentStructureAudio.AddRange(Row(ids[0], seed.Parse, seed.Node, "z.wav", 1), Row(ids[1], seed.Parse, seed.Node, "Z.wav", 1));
                var other = Guid.NewGuid(); db.DocumentParses.Add(new() { Id = other, DocumentFileId = seed.File, Status = DocumentParseStatus.Parsed, ParsedAt = DateTimeOffset.UtcNow.AddDays(-1) });
                db.DocumentStructureAudio.Add(Row(ids[2], other, seed.Node, "cross.wav", 1)); await db.SaveOwnedChangesAsync();
            }
            using var json = JsonDocument.Parse(await client.GetStringAsync(ListUrl(seed.Node)));
            Assert.Equal(new[] { ids[1], seed.Audio, ids[0] }, json.RootElement.GetProperty("data").GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
        }
        finally { await Cleanup(factory.Services, seed); }
    }

    [Theory]
    [InlineData(1)][InlineData(2)]
    public async Task DatabaseCancellation_BeforeCopyOrDuringAdmissionNeverSucceeds(int cancelledRead)
    {
        var bytes = PlayableWave.Bytes(); var node = Guid.NewGuid(); var audio = Guid.NewGuid(); var parse = Guid.NewGuid(); var file = Guid.NewGuid(); var scratch = new Scratch();
        var membership = new InternalAudioMembership(new(file, parse, node, 0, 1, parse), [new(audio, parse, node, "file.wav", "audio/wav", bytes.Length, Key(parse, audio))]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var repository = new Mock<IInternalAudioRepository>(MockBehavior.Strict);
        repository.Setup(x => x.ReadAudioAsync(node, audio, It.IsAny<CancellationToken>())).Returns(async (Guid _, Guid? _, CancellationToken cancellation) =>
        {
            if (Interlocked.Increment(ref calls) == cancelledRead) { entered.SetResult(); await Task.Delay(Timeout.Infinite, cancellation); }
            return membership;
        });
        var oss = new Mock<IOssService>(MockBehavior.Strict);
        oss.Setup(x => x.CopyToAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).Returns((string _, Stream stream, CancellationToken cancellation) => stream.WriteAsync(bytes, cancellation).AsTask());
        using var factory = Factory(services => { services.RemoveAll<IInternalAudioRepository>(); services.AddSingleton(repository.Object); services.RemoveAll<IOssService>(); services.AddSingleton(oss.Object); services.RemoveAll<AudioScratchSpace>(); services.AddSingleton<AudioScratchSpace>(scratch); });
        using var client = factory.CreateClient(); var tokens = factory.Services.GetRequiredService<InternalAudioTokens>();
        var link = ListUrl(node) + $"/{audio:D}/content?token=" + tokens.Create(new(file, parse, node, audio), DateTimeOffset.UtcNow.AddMinutes(2));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var reading = client.GetAsync(link, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.True(scratch.Last is null || scratch.Last.Disposed);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task OutputFailureOrDisconnect_AbortsAndReleasesScratch(bool cancel)
    {
        var bytes = PlayableWave.Bytes(); var scratch = new Scratch(); var oss = new Mock<IOssService>(MockBehavior.Strict);
        oss.Setup(x => x.CopyToAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).Returns((string _, Stream stream, CancellationToken cancellation) => stream.WriteAsync(bytes, cancellation).AsTask());
        using var factory = Factory(services => { services.RemoveAll<IOssService>(); services.AddSingleton(oss.Object); });
        var seed = await SeedAsync(factory.Services, bytes); using var cancellation = new CancellationTokenSource();
        try
        {
            using var scope = factory.Services.CreateScope(); var tokens = factory.Services.GetRequiredService<InternalAudioTokens>();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider }; var started = new Started(); var lifetime = new Lifetime(cancellation.Token);
            context.Features.Set<IHttpResponseFeature>(started); context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
            context.Request.QueryString = new("?token=" + tokens.Create(new(seed.File, seed.Tree, seed.Node, seed.Audio), DateTimeOffset.UtcNow.AddMinutes(2)));
            context.Response.Body = new FaultOutput(() => { started.Value = true; if (cancel) cancellation.Cancel(); });
            var execute = InternalAudioEndpoints.Content(seed.Node.ToString("D"), seed.Audio.ToString("D"), context,
                scope.ServiceProvider.GetRequiredService<IInternalAudioRepository>(), factory.Services.GetRequiredService<InternalAudioSettings>(), tokens,
                scope.ServiceProvider.GetRequiredService<StructureAudioStore>(), scope.ServiceProvider.GetRequiredService<IOptions<DocumentAudioOptions>>(), scratch, factory.Services.GetRequiredService<ILoggerFactory>());
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execute); else await execute;
            Assert.True(lifetime.Aborted); Assert.True(scratch.Last?.Disposed); Assert.Equal("audio/wav", context.Response.ContentType);
        }
        finally { await Cleanup(factory.Services, seed); }
    }

    [Fact]
    public async Task StorageDeadline_PropagatesToCopyAndCleansScratch()
    {
        var scratch = new Scratch(); var observed = CancellationToken.None; var oss = new Mock<IOssService>(MockBehavior.Strict);
        oss.Setup(x => x.CopyToAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).Returns(async (string _, Stream _, CancellationToken cancellation) => { observed = cancellation; await Task.Delay(Timeout.Infinite, cancellation); });
        var store = new StructureAudioStore(() => oss.Object, new(true, null), NullLogger<StructureAudioStore>.Instance);
        var parse = Guid.NewGuid(); var audio = Guid.NewGuid(); var timer = Stopwatch.StartNew();
        await Assert.ThrowsAsync<AudioStorageException>(() => store.BufferAsync(new(audio, parse, Guid.NewGuid(), "file.wav", "audio/wav", 1, Key(parse, audio)), 100, scratch, CancellationToken.None));
        Assert.True(observed.IsCancellationRequested); Assert.True(scratch.Last?.Disposed); Assert.InRange(timer.Elapsed.TotalSeconds, 25, 40);
    }

    [Fact]
    public async Task PostAdmissionRemoval_CanFinishExistingStreamButNeverAuthorizesNextRequest()
    {
        var bytes = PlayableWave.Bytes(); var scratch = new Scratch(); var oss = new Mock<IOssService>(MockBehavior.Strict);
        oss.Setup(x => x.CopyToAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).Returns((string _, Stream stream, CancellationToken cancellation) => stream.WriteAsync(bytes, cancellation).AsTask());
        using var factory = Factory(services => { services.RemoveAll<IOssService>(); services.AddSingleton(oss.Object); });
        using var client = factory.CreateClient(); var seed = await SeedAsync(factory.Services, bytes);
        try
        {
            var link = await Link(client, seed.Node); using var scope = factory.Services.CreateScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.QueryString = new(new Uri("http://test" + link).Query);
            var output = new MutationOutput(() => Mutate(factory.Services, seed, "unlink")); context.Response.Body = output;
            await InternalAudioEndpoints.Content(seed.Node.ToString("D"), seed.Audio.ToString("D"), context,
                scope.ServiceProvider.GetRequiredService<IInternalAudioRepository>(), factory.Services.GetRequiredService<InternalAudioSettings>(), factory.Services.GetRequiredService<InternalAudioTokens>(),
                scope.ServiceProvider.GetRequiredService<StructureAudioStore>(), scope.ServiceProvider.GetRequiredService<IOptions<DocumentAudioOptions>>(), scratch, factory.Services.GetRequiredService<ILoggerFactory>());
            Assert.Equal(bytes, output.ToArray()); Assert.True(scratch.Last?.Disposed);
            using var next = await client.GetAsync(link); Assert.Equal(HttpStatusCode.NotFound, next.StatusCode);
            using (var mutation = factory.Services.CreateScope())
                await mutation.ServiceProvider.GetRequiredService<DocthecaDbContext>().DocumentStructureAudio.Where(x => x.Id == seed.Audio).ExecuteUpdateAsync(set => set.SetProperty(x => x.NodeId, (Guid?)seed.Node));
            using var relinked = await client.GetAsync(link); Assert.Equal(HttpStatusCode.OK, relinked.StatusCode);
            Assert.Equal(bytes, await relinked.Content.ReadAsByteArrayAsync());
        }
        finally { await Cleanup(factory.Services, seed); }
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task RealScratch_IsDeletedAfterSuccessOrFailure(bool fail)
    {
        var scratch = new DiskScratch(); var parse = Guid.NewGuid(); var audio = Guid.NewGuid(); var oss = new Mock<IOssService>(MockBehavior.Strict);
        oss.Setup(x => x.CopyToAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).Returns(async (string _, Stream target, CancellationToken cancellation) =>
        { await target.WriteAsync(new byte[1], cancellation); if (fail) throw new IOException("private-provider-canary"); });
        var store = new StructureAudioStore(() => oss.Object, new(true, null), NullLogger<StructureAudioStore>.Instance);
        var row = new InternalAudioObject(audio, parse, Guid.NewGuid(), "file.wav", "audio/wav", 1, Key(parse, audio));
        if (fail) await Assert.ThrowsAsync<AudioStorageException>(() => store.BufferAsync(row, 100, scratch, CancellationToken.None));
        else { await using var stream = await store.BufferAsync(row, 100, scratch, CancellationToken.None); Assert.True(stream.CanSeek); }
        Assert.NotNull(scratch.Name); Assert.False(File.Exists(scratch.Name));
    }

    [Theory]
    [InlineData("valid")] [InlineData("foreign-document")] [InlineData("foreign-audio")] [InlineData("wrong-type")] [InlineData("unproven-legacy")]
    public async Task DocumentPrefixBuffer_UsesOnlyVerifiedServerOwnerAndAudioIdentity(string mutation)
    {
        var document = Guid.NewGuid(); var id = Guid.NewGuid(); var scratch = new Scratch(); var oss = new Mock<IOssService>(MockBehavior.Strict);
        var key = $"audio/documents/{document:N}/{id:N}.wav";
        if (mutation == "foreign-document") key = $"audio/documents/{Guid.NewGuid():N}/{id:N}.wav";
        if (mutation == "foreign-audio") key = $"audio/documents/{document:N}/{Guid.NewGuid():N}.wav";
        if (mutation == "unproven-legacy") key = $"audio/{Guid.NewGuid():N}/{id:N}.wav";
        var row = new InternalAudioObject(id, null, Guid.NewGuid(), "generated.wav", mutation == "wrong-type" ? "audio/mpeg" : "audio/wav", 1, key, document);
        var store = new StructureAudioStore(() => oss.Object, new(true, null), NullLogger<StructureAudioStore>.Instance);
        if (mutation == "valid")
        {
            oss.Setup(x => x.CopyToAsync(key, It.IsAny<Stream>(), It.IsAny<CancellationToken>())).Returns((string _, Stream stream, CancellationToken ct) => stream.WriteAsync(new byte[1], ct).AsTask());
            await using var bytes = await store.BufferAsync(row, 10, scratch, default); Assert.Equal(1, bytes.Length);
        }
        else { await Assert.ThrowsAsync<AudioStorageException>(() => store.BufferAsync(row, 10, scratch, default)); oss.VerifyNoOtherCalls(); Assert.Null(scratch.Last); }
    }

    [Fact]
    public async Task DatabaseErrors_UseSafeJsonAndByteFailuresWithoutProviderDiagnostics()
    {
        var repository = new Mock<IInternalAudioRepository>(MockBehavior.Strict); var logs = new Capture();
        repository.Setup(x => x.ReadAudioAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("db-provider-private-canary"));
        using var factory = Factory(services => { services.RemoveAll<IInternalAudioRepository>(); services.AddSingleton(repository.Object); services.AddSingleton<ILoggerProvider>(logs); });
        using var client = factory.CreateClient(); var node = Guid.NewGuid(); var audio = Guid.NewGuid();
        var tokens = factory.Services.GetRequiredService<InternalAudioTokens>();
        foreach (var path in new[] { ListUrl(node), ListUrl(node) + $"/{audio:D}/content?token=" + tokens.Create(new(Guid.NewGuid(), Guid.NewGuid(), node, audio), DateTimeOffset.UtcNow.AddMinutes(2)) })
        {
            using var response = await client.GetAsync(path); Headers(response); Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Safe(await response.Content.ReadAsStringAsync() + string.Join('\n', logs.Messages), "db-provider-private-canary");
        }
    }

    [Fact]
    public async Task UnsupportedProvider_NeverFallsBackToLegacyDownload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "doctheca-unsupported-audio-" + Guid.NewGuid().ToString("N"));
        try
        {
            IOssService provider = new LocalFileOssService(directory);
            using var target = new MemoryStream();
            await Assert.ThrowsAsync<NotSupportedException>(() => provider.CopyToAsync("audio/not-present.wav", target, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class DiskScratch : AudioScratchSpace
    { public string? Name; public override Stream Open() { var stream = (FileStream)base.Open(); Name = stream.Name; return stream; } }
    private sealed class MutationOutput(Func<Task> mutate) : MemoryStream
    {
        private bool _mutated;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { if (!_mutated) { _mutated = true; await mutate(); } await base.WriteAsync(buffer, cancellationToken); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed class Started : HttpResponseFeature { public bool Value; public override bool HasStarted => Value; }
    private sealed class Lifetime(CancellationToken token) : IHttpRequestLifetimeFeature
    { public bool Aborted; public CancellationToken RequestAborted { get; set; } = token; public void Abort() => Aborted = true; }
    private sealed class FaultOutput(Action started) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { started(); throw new IOException("output-private-canary"); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
