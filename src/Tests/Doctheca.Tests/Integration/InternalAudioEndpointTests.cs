using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Doctheca.Common.Oss;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Service.Audio;
using Doctheca.Service.InternalApi;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Doctheca.Tests.Integration;

[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed partial class InternalAudioEndpointTests(PostgreSqlFixture database, MinioFixture minio)
    : ServiceMantleIntegrationTestBase(database), IClassFixture<MinioFixture>
{
    private readonly string _bucket = "internal-audio-" + Guid.NewGuid().ToString("N")[..12];
    private static string ListUrl(Guid node) => $"/internal/v1/structure-nodes/{node:D}/audio";
    private WebApplicationFactory<Program> Factory(Action<IServiceCollection>? configure = null,
        Dictionary<string, string?>? extra = null)
    {
        var settings = minio.Settings(_bucket);
        settings["InternalApi:Enabled"] = "true";
        settings["Oss:PublicBaseUrl"] = "invalid-public-url";
        foreach (var (key, value) in extra ?? []) settings[key] = value;
        return CreateFactory(settings: settings, configureTestServices: services =>
        {
            services.RemoveAll<IOssService>();
            services.AddScoped<IOssService>(sp => new S3OssService(sp.GetRequiredService<IOptions<OssOptions>>().Value));
            configure?.Invoke(services);
        });
    }
    internal sealed record Seed(Guid Definition, Guid Level, Guid File, Guid Parse, Guid Node, Guid Child, Guid Empty, Guid Audio);
    internal static async Task<Seed> SeedAsync(IServiceProvider services, byte[] bytes)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var s = new Seed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        db.StructureDefinitions.Add(new() { Id = s.Definition, Name = "definition" });
        db.StructureLevels.Add(new() { Id = s.Level, DefinitionId = s.Definition, Name = "level" });
        db.DocumentFiles.Add(new() { Id = s.File, FileName = "document-canary", FilePath = "private-storage-canary", StructureDefinitionId = s.Definition });
        db.DocumentParses.Add(new() { Id = s.Parse, DocumentFileId = s.File, Status = DocumentParseStatus.Parsed, ParsedAt = DateTimeOffset.UtcNow });
        foreach (var (id, parent) in new[] { (s.Node, (Guid?)null), (s.Child, (Guid?)s.Node), (s.Empty, (Guid?)null) })
            db.DocumentStructureNodes.Add(new() { Id = id, ParseId = s.Parse, LevelId = s.Level, ParentId = parent, Name = "node", StartSequence = 0, EndSequence = 1 });
        db.DocumentStructureAudio.AddRange(
            Row(s.Audio, s.Parse, s.Node, "folder/声.wav", bytes.Length),
            Row(Guid.NewGuid(), s.Parse, s.Child, "descendant.wav", bytes.Length),
            Row(Guid.NewGuid(), s.Parse, null, "unlinked.wav", bytes.Length));
        await db.SaveChangesAsync();
        return s;
    }
    private static DocumentStructureAudioEntity Row(Guid id, Guid parse, Guid? node, string path, long size) => new()
    { Id = id, ParseId = parse, NodeId = node, SourcePath = path, SizeBytes = size, ContentType = "audio/wav", ObjectKey = Key(parse, id), MatchStatus = "matched" };
    private static string Key(Guid parse, Guid id) => $"audio/{parse:N}/{id:N}.wav";
    private static async Task Cleanup(IServiceProvider services, Seed s)
    {
        using var scope = services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        await db.DocumentFiles.Where(x => x.Id == s.File).ExecuteDeleteAsync();
        await db.StructureDefinitions.Where(x => x.Id == s.Definition).ExecuteDeleteAsync();
    }
    private static void Keys(JsonElement value, params string[] expected) =>
        Assert.Equal(expected.Order(), value.EnumerateObject().Select(x => x.Name).Order());
    private static void Safe(string value, params string[] canaries) =>
        Assert.True(canaries.All(canary => !value.Contains(canary, StringComparison.Ordinal)), "Sensitive canary appeared in output.");
    private static void Headers(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Null(response.Headers.Location);
    }
    private static async Task<string> Link(HttpClient client, Guid node)
    {
        using var response = await client.GetAsync(ListUrl(node));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("data").GetProperty("items")[0].GetProperty("playback").GetProperty("url").GetString()!;
    }

    [Fact]
    public async Task RealPostgresAndMinio_DirectListExactFieldsRangesAndNoPublicAddressRequirement()
    {
        var logs = new Capture();
        using var factory = Factory(services => services.AddSingleton<ILoggerProvider>(logs));
        using var client = factory.CreateClient(); var bytes = PlayableWave.Bytes();
        var s = await SeedAsync(factory.Services, bytes);
        await minio.PutAsync(_bucket, Key(s.Parse, s.Audio), bytes);
        try
        {
            using var response = await client.GetAsync(ListUrl(s.Node)); Headers(response);
            var text = await response.Content.ReadAsStringAsync();
            Safe(text, _bucket, minio.InternalEndpoint, minio.AccessKey, minio.SecretKey, Key(s.Parse, s.Audio), "private-storage-canary", "folder/");
            using var json = JsonDocument.Parse(text); Keys(json.RootElement, "success", "data");
            var data = json.RootElement.GetProperty("data"); Keys(data, "documentFileId", "parseId", "nodeId", "items");
            Assert.Equal(s.File, data.GetProperty("documentFileId").GetGuid()); Assert.Equal(s.Parse, data.GetProperty("parseId").GetGuid());
            var item = Assert.Single(data.GetProperty("items").EnumerateArray()); Keys(item, "id", "fileName", "contentType", "sizeBytes", "playback");
            Assert.Equal("声.wav", item.GetProperty("fileName").GetString()); Assert.Equal(bytes.Length, item.GetProperty("sizeBytes").GetInt64());
            var playback = item.GetProperty("playback"); Keys(playback, "url", "expiresAt", "expiresInSeconds");
            Assert.Equal(300, playback.GetProperty("expiresInSeconds").GetInt32());
            var link = playback.GetProperty("url").GetString()!;
            Assert.True(link.StartsWith(ListUrl(s.Node) + $"/{s.Audio:D}/content?token=", StringComparison.Ordinal), "Link was not same-origin.");
            using var full = await client.GetAsync(link); Headers(full); Assert.Equal(HttpStatusCode.OK, full.StatusCode);
            Assert.Equal("audio/wav", full.Content.Headers.ContentType?.MediaType); Assert.Equal(bytes.Length, full.Content.Headers.ContentLength);
            Assert.Equal(bytes, await full.Content.ReadAsByteArrayAsync());
            foreach (var (range, status, expected) in new[] { ("bytes=1-9", HttpStatusCode.PartialContent, bytes[1..10]), ("bytes=-8", HttpStatusCode.PartialContent, bytes[^8..]), ("bytes=99999999-", HttpStatusCode.RequestedRangeNotSatisfiable, Array.Empty<byte>()) })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, link); request.Headers.TryAddWithoutValidation("Range", range);
                using var ranged = await client.SendAsync(request); Headers(ranged); Assert.Equal(status, ranged.StatusCode);
                Assert.Equal(expected, await ranged.Content.ReadAsByteArrayAsync());
            }
            using var empty = JsonDocument.Parse(await client.GetStringAsync(ListUrl(s.Empty)));
            Assert.Empty(empty.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());
            using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
            Assert.Equal(3, await db.DocumentStructureAudio.CountAsync(x => x.ParseId == s.Parse));
            Safe(string.Join('\n', logs.Messages), link, QueryHelpers.ParseQuery(new Uri("http://test" + link).Query)["token"].ToString(), _bucket, minio.SecretKey, minio.AccessKey, Key(s.Parse, s.Audio));
        }
        finally { await Cleanup(factory.Services, s); }
    }

    [Theory]
    [InlineData("DocumentAudio:Enabled", "false")]
    [InlineData("DocumentAudio:PlaybackLinkSeconds", "59")]
    [InlineData("DocumentAudio:PlaybackLinkSeconds", "3601")]
    [InlineData("DocumentAudio:PlaybackLinkSeconds", "bad")]
    [InlineData("Oss:InternalEndpoint", "")]
    [InlineData("Oss:AccessKey", "")]
    [InlineData("Oss:SecretKey", "")]
    [InlineData("Oss:BucketName", "")]
    public async Task Misconfiguration_IsSafeAndDoesNotConstructStorage(string key, string value)
    {
        var oss = new Mock<IOssService>(MockBehavior.Strict);
        using var factory = Factory(services => { services.RemoveAll<IOssService>(); services.AddSingleton(oss.Object); }, new() { [key] = value });
        using var client = factory.CreateClient(); var s = await SeedAsync(factory.Services, PlayableWave.Bytes());
        try
        {
            using var response = await client.GetAsync(ListUrl(s.Node)); Headers(response); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("DOCTHECA_INTERNAL_AUDIO_UNAVAILABLE", await response.Content.ReadAsStringAsync());
            using var missing = await client.GetAsync(ListUrl(Guid.NewGuid())); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            oss.VerifyNoOtherCalls();
        }
        finally { await Cleanup(factory.Services, s); }
    }

    [Fact]
    public async Task InputAndTokens_RejectBeforeStorageAndNeverEchoCapabilities()
    {
        var provider = new EphemeralDataProtectionProvider(); var tokens = new InternalAudioTokens(provider, TimeProvider.System);
        var oss = new Mock<IOssService>(MockBehavior.Strict); var logs = new Capture();
        using var factory = Factory(services => { services.RemoveAll<IOssService>(); services.AddSingleton(oss.Object); services.AddSingleton(tokens); services.AddSingleton<ILoggerProvider>(logs); });
        using var client = factory.CreateClient(); var s = await SeedAsync(factory.Services, PlayableWave.Bytes());
        try
        {
            var binding = new InternalAudioBinding(s.File, s.Parse, s.Node, s.Audio);
            var valid = tokens.Create(binding, DateTimeOffset.UtcNow.AddMinutes(2));
            var expired = tokens.Create(binding, DateTimeOffset.UtcNow.AddSeconds(-1));
            var wrongPurpose = WebEncoders.Base64UrlEncode(provider.CreateProtector("unrelated").ToTimeLimitedDataProtector().Protect(new byte[65], DateTimeOffset.UtcNow.AddMinutes(2)));
            var fresh = new InternalAudioTokens(TimeProvider.System).Create(binding, DateTimeOffset.UtcNow.AddMinutes(2));
            var content = ListUrl(s.Node) + $"/{s.Audio:D}/content";
            foreach (var token in new[] { "", "*", valid + "=", valid[..^1], expired, wrongPurpose, fresh, new string('x', 2049) })
            {
                using var response = await client.GetAsync(content + "?token=" + Uri.EscapeDataString(token)); Headers(response);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Safe(await response.Content.ReadAsStringAsync(), "private-storage-canary");
            }
            foreach (var path in new[] { content, ListUrl(Guid.NewGuid()) + $"/{s.Audio:D}/content?token=" + valid, ListUrl(s.Node) + $"/{Guid.NewGuid():D}/content?token=" + valid })
            { using var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); }
            foreach (var path in new[] { ListUrl(s.Node) + "?x=1", "/internal/v1/structure-nodes/%20" + s.Node.ToString("D") + "%20/audio", ListUrl(s.Node) + "?x=1&x=2", content + "?x=1", content + "?token=x&token=y", "/internal/v1/structure-nodes/invalid/audio", ListUrl(s.Node) + "/invalid/content?token=x" })
            { using var response = await client.GetAsync(path); Headers(response); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); }
            foreach (var method in new[] { HttpMethod.Head, HttpMethod.Post, HttpMethod.Delete })
            { using var request = new HttpRequestMessage(method, content + "?token=" + valid); using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); }
            oss.VerifyNoOtherCalls(); Safe(string.Join('\n', logs.Messages), valid, expired, wrongPurpose, fresh);
            var decoded = provider.CreateProtector(InternalAudioTokens.Purpose).ToTimeLimitedDataProtector().Unprotect(WebEncoders.Base64UrlDecode(valid), out var expiry);
            Assert.Equal(65, decoded.Length); Assert.Equal(1, decoded[0]); Assert.Equal(s.File, new Guid(decoded.AsSpan(1, 16)));
            Assert.True(expiry > DateTimeOffset.UtcNow);
            Assert.Throws<CryptographicException>(() => provider.CreateProtector(InternalAudioTokens.Purpose).ToTimeLimitedDataProtector().Unprotect(WebEncoders.Base64UrlDecode(expired), out _));
            Assert.False(new InternalAudioTokens(TimeProvider.System).TryRead(valid, s.Node, s.Audio, out _));
        }
        finally { await Cleanup(factory.Services, s); }
    }

    [Theory]
    [InlineData("unlink")][InlineData("reassign")][InlineData("audio")][InlineData("node")][InlineData("parse")][InlineData("document")][InlineData("current")][InlineData("replace")]
    public async Task CommittedRevocation_IsNotALeaseAndNeverReadsStorage(string mutation)
    {
        var oss = new Mock<IOssService>(MockBehavior.Strict);
        using var factory = Factory(services => { services.RemoveAll<IOssService>(); services.AddSingleton(oss.Object); });
        using var client = factory.CreateClient(); var s = await SeedAsync(factory.Services, PlayableWave.Bytes());
        try
        {
            var link = await Link(client, s.Node); await Mutate(factory.Services, s, mutation);
            using var response = await client.GetAsync(link); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Headers(response);
            oss.VerifyNoOtherCalls();
        }
        finally { await Cleanup(factory.Services, s); }
    }

    private static async Task Mutate(IServiceProvider services, Seed s, string mutation)
    {
        using var scope = services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var selected = db.DocumentStructureAudio.Where(x => x.Id == s.Audio);
        if (mutation == "unlink") await selected.ExecuteUpdateAsync(set => set.SetProperty(x => x.NodeId, (Guid?)null));
        if (mutation == "reassign") await selected.ExecuteUpdateAsync(set => set.SetProperty(x => x.NodeId, (Guid?)s.Child));
        if (mutation == "size") await selected.ExecuteUpdateAsync(set => set.SetProperty(x => x.SizeBytes, x => x.SizeBytes + 1));
        if (mutation == "key") await selected.ExecuteUpdateAsync(set => set.SetProperty(x => x.ObjectKey, "changed-private-canary"));
        if (mutation == "type") await selected.ExecuteUpdateAsync(set => set.SetProperty(x => x.ContentType, "audio/mpeg"));
        if (mutation == "audio") await selected.ExecuteDeleteAsync();
        if (mutation == "node") await db.DocumentStructureNodes.Where(x => x.Id == s.Node || x.ParentId == s.Node).ExecuteDeleteAsync();
        if (mutation == "parse") await db.DocumentParses.Where(x => x.Id == s.Parse).ExecuteDeleteAsync();
        if (mutation == "document") await db.DocumentFiles.Where(x => x.Id == s.File).ExecuteDeleteAsync();
        if (mutation == "replace")
            await scope.ServiceProvider.GetRequiredService<IDocumentStructureRepository>().ReplaceAsync(s.Parse,
                [new() { Id = Guid.NewGuid(), ParseId = s.Parse, LevelId = s.Level, Name = "replacement", StartSequence = 0, EndSequence = 1 }]);
        if (mutation == "current")
        {
            var parse = Guid.NewGuid(); db.DocumentParses.Add(new() { Id = parse, DocumentFileId = s.File, Status = DocumentParseStatus.Parsed, ParsedAt = DateTimeOffset.UtcNow.AddDays(1) });
            db.DocumentStructureNodes.Add(new() { ParseId = parse, LevelId = s.Level, Name = "current", StartSequence = 0, EndSequence = 1 }); await db.SaveChangesAsync();
        }
    }

    [Theory]
    [InlineData("unlink")][InlineData("reassign")][InlineData("audio")][InlineData("node")][InlineData("parse")][InlineData("document")][InlineData("current")][InlineData("replace")][InlineData("key")][InlineData("size")][InlineData("type")][InlineData("expiry")]
    public async Task BufferingRechecksFreshMembershipAndExpiry(string mutation)
    {
        Seed? seed = null; IServiceProvider? services = null; var clock = new Clock(); var scratch = new Scratch();
        var bytes = PlayableWave.Bytes(); var oss = new Mock<IOssService>(MockBehavior.Strict);
        oss.Setup(x => x.CopyToAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).Returns(async (string _, Stream target, CancellationToken cancellation) =>
        {
            await target.WriteAsync(bytes, cancellation);
            if (mutation == "expiry") clock.Now = clock.Now.AddHours(1); else await Mutate(services!, seed!, mutation);
        });
        using var factory = Factory(collection =>
        {
            collection.RemoveAll<IOssService>(); collection.AddSingleton(oss.Object);
            collection.RemoveAll<TimeProvider>(); collection.AddSingleton<TimeProvider>(clock);
            collection.RemoveAll<AudioScratchSpace>(); collection.AddSingleton<AudioScratchSpace>(scratch);
        });
        services = factory.Services; seed = await SeedAsync(services, bytes); using var client = factory.CreateClient();
        try
        {
            var link = await Link(client, seed.Node); using var response = await client.GetAsync(link);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.True(scratch.Last?.Disposed); Headers(response);
            oss.Verify(x => x.CopyToAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { await Cleanup(services, seed); }
    }

    [Theory]
    [InlineData("swallowed")][InlineData("short")][InlineData("oversized")][InlineData("throw")][InlineData("wrapped")][InlineData("disk")][InlineData("missing")]
    public async Task TransferFailures_AreFixedAndDisposeScratch(string failure)
    {
        var bytes = PlayableWave.Bytes(); var scratch = new Scratch { FailOpen = failure == "disk" }; var logs = new Capture();
        var oss = new Mock<IOssService>(MockBehavior.Strict);
        oss.Setup(x => x.CopyToAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).Returns(async (string _, Stream target, CancellationToken cancellation) =>
        {
            if (failure is "throw" or "wrapped" or "missing") throw new IOException("provider-private-canary", failure == "wrapped" ? new OperationCanceledException() : null);
            if (failure == "swallowed")
            {
                await target.WriteAsync(bytes, cancellation);
                try { await target.WriteAsync(new byte[1], cancellation); } catch (AudioStorageException) { }
                return;
            }
            await target.WriteAsync(failure == "oversized" ? bytes.Concat(new byte[1]).ToArray() : bytes[..^1], cancellation);
        });
        using var factory = Factory(services =>
        {
            services.RemoveAll<IOssService>(); services.AddSingleton(oss.Object); services.AddSingleton<ILoggerProvider>(logs);
            services.RemoveAll<AudioScratchSpace>(); services.AddSingleton<AudioScratchSpace>(scratch);
        });
        using var client = factory.CreateClient(); var s = await SeedAsync(factory.Services, bytes);
        try
        {
            using var response = await client.GetAsync(await Link(client, s.Node)); Headers(response);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Contains("Audio is unavailable.", await response.Content.ReadAsStringAsync());
            Assert.True(scratch.Last is null || scratch.Last.Disposed); Safe(string.Join('\n', logs.Messages), "provider-private-canary", Key(s.Parse, s.Audio));
        }
        finally { await Cleanup(factory.Services, s); }
    }

    [Theory]
    [InlineData("key")][InlineData("type")][InlineData("size")][InlineData("limit")]
    public async Task InvalidStoredIdentity_RejectsBeforeObjectIo(string invalid)
    {
        var oss = new Mock<IOssService>(MockBehavior.Strict);
        using var factory = Factory(services => { services.RemoveAll<IOssService>(); services.AddSingleton(oss.Object); });
        using var client = factory.CreateClient(); var s = await SeedAsync(factory.Services, PlayableWave.Bytes());
        try
        {
            var link = await Link(client, s.Node);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>(); var selected = db.DocumentStructureAudio.Where(x => x.Id == s.Audio);
                if (invalid == "size") await selected.ExecuteUpdateAsync(set => set.SetProperty(x => x.SizeBytes, 0));
                else if (invalid == "limit") await selected.ExecuteUpdateAsync(set => set.SetProperty(x => x.SizeBytes, long.MaxValue));
                else await Mutate(factory.Services, s, invalid);
            }
            using var response = await client.GetAsync(link); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); oss.VerifyNoOtherCalls();
        }
        finally { await Cleanup(factory.Services, s); }
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task SnapshotReads_NoWritesOrWriterLocksAndCancellation(bool cancel)
    {
        var probe = new Probe(); using var factory = Factory(services => services.AddDbContext<DocthecaDbContext>(options => options.AddInterceptors(probe)));
        var s = await SeedAsync(factory.Services, PlayableWave.Bytes());
        try
        {
            using var scope = factory.Services.CreateScope(); var repository = scope.ServiceProvider.GetRequiredService<IInternalAudioRepository>();
            probe.Active = true; using var cancellation = new CancellationTokenSource(); var reading = repository.ReadAudioAsync(s.Node, null, cancellation.Token);
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Mutate(factory.Services, s, "unlink");
            if (cancel) cancellation.Cancel(); probe.Release.TrySetResult();
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
            else { var read = await reading; Assert.NotNull(read); Assert.Single(read.Items); }
            Assert.True(probe.ReadOnly); Assert.Equal(IsolationLevel.RepeatableRead, probe.Isolation);
            probe.Active = false; Assert.Empty((await repository.ReadAudioAsync(s.Node, null, CancellationToken.None))!.Items);
        }
        finally { probe.Release.TrySetResult(); probe.Active = false; await Cleanup(factory.Services, s); }
    }

    [Fact]
    public async Task RequestCancellation_ReleasesUnderlyingCopyAndScratchWithoutSuccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var scratch = new Scratch();
        var oss = new Mock<IOssService>(MockBehavior.Strict);
        oss.Setup(x => x.CopyToAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).Returns(async (string _, Stream target, CancellationToken cancellation) =>
        {
            entered.SetResult(); try { await Task.Delay(Timeout.Infinite, cancellation); }
            catch (OperationCanceledException) { released.SetResult(); throw new IOException("wrapped-private-canary"); }
        });
        using var factory = Factory(services => { services.RemoveAll<IOssService>(); services.AddSingleton(oss.Object); services.RemoveAll<AudioScratchSpace>(); services.AddSingleton<AudioScratchSpace>(scratch); });
        using var client = factory.CreateClient(); var s = await SeedAsync(factory.Services, PlayableWave.Bytes());
        try
        {
            var link = await Link(client, s.Node); using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reading = client.GetAsync(link, cancellation.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading); await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(scratch.Last?.Disposed);
        }
        finally { await Cleanup(factory.Services, s); }
    }

    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Scratch : AudioScratchSpace
    {
        public Tracked? Last; public bool FailOpen;
        public override Stream Open() { if (FailOpen) throw new IOException("scratch-private-canary"); return Last = new(); }
    }
    private sealed class Tracked : MemoryStream { public bool Disposed; protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); } }
    private sealed class Capture : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new(); public void Dispose() { }
        public ILogger CreateLogger(string categoryName) => new Logger(Messages);
        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception) + exception?.ToString());
        }
    }
    private sealed class Probe : DbCommandInterceptor
    {
        public bool Active; public bool ReadOnly; public IsolationLevel Isolation;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Active)
            {
                Assert.DoesNotContain("FOR UPDATE", command.CommandText); Isolation = command.Transaction!.IsolationLevel;
                if (command.CommandText.Contains("document_structure_audio", StringComparison.Ordinal)) { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            }
            return result;
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (Active && command.CommandText == "SET TRANSACTION READ ONLY") ReadOnly = true; return ValueTask.FromResult(result); }
    }
}
