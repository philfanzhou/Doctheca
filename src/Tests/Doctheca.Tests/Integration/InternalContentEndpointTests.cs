using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Doctheca.Common.Oss;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Database.Repositories;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Service.StructaDoc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Doctheca.Tests.Integration;

[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed class InternalContentEndpointTests(PostgreSqlFixture database) : ServiceMantleIntegrationTestBase(database)
{
    private static string Url(Guid node) => $"/internal/v1/structure-nodes/{node:D}/content";
    private static string ImageUrl(Guid node, Guid image, string revision) => $"/internal/v1/structure-nodes/{node:D}/images/{image:D}/content?revision={revision}";
    private WebApplicationFactory<Program> Factory(Action<IServiceCollection>? services = null) =>
        CreateFactory(configureTestServices: services, settings: new Dictionary<string, string?> { ["InternalApi:Enabled"] = "true" });

    [Fact]
    public async Task SmallPages_CoverInclusiveRangeExactlyOnceWithGapsAndAllowListedFields()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory);
        try
        {
            var seen = new List<Guid>();
            string? revision = null;
            int? after = null;
            var pageCount = 0;
            do
            {
                using var response = await client.GetAsync(Url(seed.Node) + "?limit=2" + (after is null ? "" : $"&afterSequence={after}&revision={revision}"));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var text = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain("private-canary", text);
                using var body = JsonDocument.Parse(text);
                Keys(body.RootElement, "success", "data");
                var data = body.RootElement.GetProperty("data");
                Keys(data, "parseId", "nodeId", "revision", "items", "nextAfterSequence", "hasMore");
                Assert.Equal(seed.Parse, data.GetProperty("parseId").GetGuid());
                Assert.Equal(seed.Node, data.GetProperty("nodeId").GetGuid());
                var current = data.GetProperty("revision").GetString()!;
                Assert.Matches("^[0-9a-f]{64}$", current);
                if (revision is not null) Assert.Equal(revision, current);
                revision = current;
                var items = data.GetProperty("items").EnumerateArray().ToArray();
                Assert.InRange(items.Length, 1, 2);
                foreach (var item in items)
                {
                    Keys(item, "id", "sequence", "blockType", "subType", "content", "contentFormat", "pageNumber", "imageId", "imageUrl");
                    var seq = item.GetProperty("sequence").GetInt32();
                    Assert.Contains(seq, new[] { 2, 3, 5, 8, 10 });
                    Assert.Equal(seq % 3 + 1, item.GetProperty("pageNumber").GetInt32());
                    seen.Add(item.GetProperty("id").GetGuid());
                    if (seq == 3)
                    {
                        Assert.Equal("<table><tr><td>untrusted text</td></tr></table>", item.GetProperty("content").GetString());
                        Assert.Equal("html", item.GetProperty("contentFormat").GetString());
                    }
                    if (seq == 5)
                    {
                        Assert.Equal(seed.Image, item.GetProperty("imageId").GetGuid());
                        Assert.Equal(ImageUrl(seed.Node, seed.Image, revision), item.GetProperty("imageUrl").GetString());
                    }
                    else Assert.Equal(JsonValueKind.Null, item.GetProperty("imageUrl").ValueKind);
                }
                after = data.GetProperty("nextAfterSequence").ValueKind == JsonValueKind.Null ? null : data.GetProperty("nextAfterSequence").GetInt32();
                Assert.Equal(after is not null, data.GetProperty("hasMore").GetBoolean());
                pageCount++;
            } while (after is not null);
            Assert.Equal(3, pageCount);
            Assert.Equal(seed.Blocks, seen);
            Assert.Equal(seen.Count, seen.Distinct().Count());
            foreach (var suffix in new[] { "", "?limit=1000", $"?afterSequence=4&revision={revision}", $"?afterSequence=10&revision={revision}" })
            {
                using var response = await client.GetAsync(Url(seed.Node) + suffix);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var count = body.RootElement.GetProperty("data").GetProperty("items").GetArrayLength();
                Assert.Equal(suffix.Contains("=10&") ? 0 : suffix.Contains("=4&") ? 3 : 5, count);
            }
        }
        finally { await CleanupAsync(factory, seed); }
    }

    [Fact]
    public async Task InvalidCursorsLimitsRevisionsAndIdentifiers_HaveFixedOutcomes()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory);
        try
        {
            var revision = await RevisionAsync(client, seed.Node);
            var invalid = new[] { "?afterSequence=2", "?afterSequence=-1", "?afterSequence=+2", "?afterSequence=%202", "?afterSequence=2.0", "?afterSequence=2147483648", "?afterSequence=", "?limit=0", "?limit=1001", "?limit=-1", "?limit=%201", "?limit=1e2", "?limit=1&limit=2", "?unknown=x", "?revision=", "?revision=" + new string('z', 64), "?revision=" + new string('a', 63), $"?afterSequence=1&revision={revision}", $"?afterSequence=11&revision={revision}", $"?revision={revision}&revision={revision}" };
            foreach (var path in invalid.Select(suffix => Url(seed.Node) + suffix).Concat(new[] { Url(seed.Node).Replace(seed.Node.ToString(), "bad-canary"), Url(seed.Node).Replace(seed.Node.ToString(), seed.Node.ToString("N")) }))
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("{\"success\":false,\"errorCode\":\"DOCTHECA_INTERNAL_INVALID\",\"message\":\"Invalid internal API request.\"}", await response.Content.ReadAsStringAsync());
            }
            using var changed = await client.GetAsync(Url(seed.Node) + "?revision=" + new string('0', 64));
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            Assert.Contains("DOCTHECA_INTERNAL_STRUCTURE_CHANGED", await changed.Content.ReadAsStringAsync());
            using var missing = await client.GetAsync(Url(Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Contains("DOCTHECA_INTERNAL_RESOURCE_NOT_FOUND", await missing.Content.ReadAsStringAsync());
            using var upper = await client.GetAsync(Url(seed.Node) + $"?afterSequence=2&revision={revision.ToUpperInvariant()}");
            Assert.Equal(HttpStatusCode.OK, upper.StatusCode);
        }
        finally { await CleanupAsync(factory, seed); }
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("order")]
    [InlineData("range")]
    [InlineData("sibling")]
    [InlineData("sync")]
    [InlineData("replace")]
    [InlineData("current")]
    [InlineData("delete")]
    [InlineData("definition")]
    public async Task MutationsBetweenPages_AreDetectedBeforeSuccessOrStorage(string mutation)
    {
        var source = new Mock<IOssService>(MockBehavior.Strict);
        using var factory = Factory(services => { services.RemoveAll<IOssService>(); services.AddSingleton(source.Object); });
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory);
        try
        {
            var revision = await RevisionAsync(client, seed.Node);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
                var selected = db.DocumentStructureNodes.Where(item => item.Id == seed.Node);
                if (mutation == "rename") await selected.ExecuteUpdateAsync(set => set.SetProperty(item => item.Name, "changed-with-same-timestamp"));
                if (mutation == "order") await selected.ExecuteUpdateAsync(set => set.SetProperty(item => item.SortOrder, 9));
                if (mutation == "range") await selected.ExecuteUpdateAsync(set => set.SetProperty(item => item.StartSequence, 3));
                if (mutation == "sibling") db.DocumentStructureNodes.Add(new DocumentStructureNodeEntity { ParseId = seed.Parse, LevelId = seed.Level, Name = "new-sibling", StartSequence = 20, EndSequence = 21 });
                if (mutation == "sync")
                {
                    var repository = scope.ServiceProvider.GetRequiredService<IDocumentParseBlockRepository>();
                    await repository.AddBlocksAsync(seed.Parse, new[] { 2, 3, 5, 8, 10 }.Select(seq => new DocumentParseBlockModel { Id = Guid.NewGuid(), ParseId = seed.Parse, Sequence = seq, BlockType = "text", ImageId = seq == 5 ? seed.Image : null }));
                }
                if (mutation == "replace") Assert.True(await scope.ServiceProvider.GetRequiredService<IDocumentStructureRepository>().ReplaceAsync(seed.Parse, [new() { Id = Guid.NewGuid(), ParseId = seed.Parse, LevelId = seed.Level, Name = "replacement", StartSequence = 2, EndSequence = 10 }]));
                if (mutation == "current")
                {
                    var parse = Guid.NewGuid();
                    db.DocumentParses.Add(new() { Id = parse, DocumentFileId = seed.File, Status = DocumentParseStatus.Parsed, ParsedAt = DateTimeOffset.UtcNow.AddDays(1) });
                    db.DocumentStructureNodes.Add(new() { ParseId = parse, LevelId = seed.Level, Name = "current", StartSequence = 2, EndSequence = 10 });
                }
                if (mutation == "delete") await db.DocumentFiles.Where(item => item.Id == seed.File).ExecuteDeleteAsync();
                if (mutation == "definition") await db.DocumentFiles.Where(item => item.Id == seed.File).ExecuteUpdateAsync(set => set.SetProperty(item => item.StructureDefinitionId, (Guid?)null));
                await db.SaveChangesAsync();
            }
            var absent = mutation is "replace" or "current" or "delete" or "definition";
            foreach (var url in new[] { Url(seed.Node) + $"?afterSequence=3&revision={revision}", ImageUrl(seed.Node, seed.Image, revision) })
            {
                using var response = await client.GetAsync(url);
                Assert.Equal(absent ? HttpStatusCode.NotFound : HttpStatusCode.Conflict, response.StatusCode);
                Assert.Contains(absent ? "DOCTHECA_INTERNAL_RESOURCE_NOT_FOUND" : "DOCTHECA_INTERNAL_STRUCTURE_CHANGED", await response.Content.ReadAsStringAsync());
            }
            source.VerifyNoOtherCalls();
        }
        finally { await CleanupAsync(factory, seed); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedImages_AnonymousLinksUseBothSourcesAndDisposeWithoutUpstreamHeaders(bool remote)
    {
        var bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        var stream = new TrackedStream(bytes);
        var content = new TrackingContent(stream);
        var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        upstream.Headers.Location = new Uri("http://private-canary.invalid/bucket/key");
        upstream.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        var handler = new AssetHandler(_ => Task.FromResult(upstream));
        var oss = new Mock<IOssService>(MockBehavior.Strict);
        oss.Setup(service => service.DownloadAsync("private-canary/key")).ReturnsAsync(stream);
        using var factory = Factory(services =>
        {
            services.RemoveAll<IOssService>(); services.AddSingleton(oss.Object);
            services.RemoveAll<IStructaDocClient>(); services.AddSingleton<IStructaDocClient>(new StructaDocClient(new HttpClient(handler) { BaseAddress = new Uri("http://upstream.invalid/") }, Options.Create(new StructaDocOptions { ApiKey = "synthetic" }), NullLogger<StructaDocClient>.Instance));
        });
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory, remote);
        try
        {
            using var page = JsonDocument.Parse(await client.GetStringAsync(Url(seed.Node)));
            var link = page.RootElement.GetProperty("data").GetProperty("items").EnumerateArray().Single(item => item.GetProperty("sequence").GetInt32() == 5).GetProperty("imageUrl").GetString()!;
            using var response = await client.GetAsync(link);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Null(response.Headers.Location);
            Assert.DoesNotContain("private-canary", response.Headers.ToString() + response.Content.Headers);
            Assert.True(stream.Disposed);
            Assert.Equal(remote, content.Disposed);
            if (remote)
            {
                Assert.Equal($"/api/v1/parse-runs/{seed.Run:D}/assets/{seed.Asset:D}/content", handler.Path);
                Assert.Equal("ApiKey", handler.Scheme);
                Assert.Equal(1, handler.Calls);
                oss.Verify(service => service.DownloadAsync(It.IsAny<string>()), Times.Never);
            }
            else Assert.Equal(0, handler.Calls);
            foreach (var route in new[] { $"/admin/document-parses/{seed.Parse}/images/{seed.Image}/content", $"/admin/document-parses/{seed.Parse}/structure/nodes/{seed.Node}/blocks" })
            {
                using var anonymous = await client.GetAsync(route);
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
                using var denied = new HttpRequestMessage(HttpMethod.Get, route);
                denied.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("user"));
                Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(denied)).StatusCode);
            }
        }
        finally { await CleanupAsync(factory, seed); }
    }

    [Fact]
    public async Task ForgedCrossNodeCrossParseAndMalformedImageReferences_NeverOpenStorage()
    {
        var oss = new Mock<IOssService>(MockBehavior.Strict);
        var remote = new Mock<IStructaDocClient>(MockBehavior.Strict);
        using var factory = Factory(services =>
        {
            services.RemoveAll<IOssService>(); services.AddSingleton(oss.Object);
            services.RemoveAll<IStructaDocClient>(); services.AddSingleton(remote.Object);
        });
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory);
        var other = await SeedAsync(factory);
        try
        {
            // Even a corrupt cross-parse block reference cannot authorize the foreign image.
            using (var scope = factory.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<DocthecaDbContext>().DocumentParseBlocks.Where(item => item.Id == seed.Blocks[0]).ExecuteUpdateAsync(set => set.SetProperty(item => item.ImageId, other.Image));
            var revision = await RevisionAsync(client, seed.Node);
            var neighborRevision = await RevisionAsync(client, seed.Neighbor);
            foreach (var url in new[] { ImageUrl(seed.Node, Guid.NewGuid(), revision), ImageUrl(seed.Node, other.Image, revision), ImageUrl(seed.Node, seed.NeighborImage, revision), ImageUrl(seed.Neighbor, seed.Image, neighborRevision), ImageUrl(Guid.NewGuid(), seed.Image, revision) })
            {
                using var response = await client.GetAsync(url);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
            foreach (var suffix in new[] { "", "?revision=x", $"?revision={revision}&limit=1", $"?revision={revision}&revision={revision}" })
            {
                using var response = await client.GetAsync(ImageUrl(seed.Node, seed.Image, revision).Split('?')[0] + suffix);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
            using var invalid = await client.GetAsync(ImageUrl(seed.Node, seed.Image, revision).Replace(seed.Image.ToString(), "bad-canary"));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            oss.VerifyNoOtherCalls(); remote.VerifyNoOtherCalls();
        }
        finally { await CleanupAsync(factory, seed); await CleanupAsync(factory, other); }
    }

    [Theory]
    [InlineData("null", 404)]
    [InlineData("missing", 404)]
    [InlineData("unavailable", 503)]
    [InlineData("broken", 404)]
    [InlineData("media", 404)]
    [InlineData("copy", 503)]
    public async Task ImageFailures_HaveSafeFixedBodiesHeadersAndLogs(string mode, int status)
    {
        var capture = new LogCapture();
        var oss = new Mock<IOssService>();
        var remote = new Mock<IStructaDocClient>();
        var stream = new TrackedStream([1]) { ThrowOnRead = mode == "copy" };
        oss.Setup(service => service.DownloadAsync(It.IsAny<string>())).Returns(() => mode switch
        {
            "null" => Task.FromResult<Stream>(null!),
            "missing" => Task.FromException<Stream>(new FileNotFoundException("private-canary/storage-key")),
            "unavailable" => Task.FromException<Stream>(new HttpRequestException("private-canary/credential")),
            _ => Task.FromResult<Stream>(stream)
        });
        using var factory = Factory(services =>
        {
            services.RemoveAll<IOssService>(); services.AddSingleton(oss.Object);
            services.RemoveAll<IStructaDocClient>(); services.AddSingleton(remote.Object);
            services.RemoveAll<ILoggerFactory>(); services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(logging => logging.AddProvider(capture)));
        });
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory, mode == "broken");
        try
        {
            if (mode is "broken" or "media")
            {
                using var scope = factory.Services.CreateScope();
                var image = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>().DocumentParseImages.Where(item => item.Id == seed.Image);
                if (mode == "broken") await image.ExecuteUpdateAsync(set => set.SetProperty(item => item.ImagePath, "private-canary/invalid-asset"));
                else await image.ExecuteUpdateAsync(set => set.SetProperty(item => item.ContentType, "image/svg+xml"));
            }
            var revision = await RevisionAsync(client, seed.Node);
            using var response = await client.GetAsync(ImageUrl(seed.Node, seed.Image, revision));
            Assert.Equal(status, (int)response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("private-canary", text + response.Headers + response.Content.Headers);
            Assert.Contains(status == 404 ? "DOCTHECA_INTERNAL_RESOURCE_NOT_FOUND" : "DOCTHECA_INTERNAL_IMAGE_UNAVAILABLE", text);
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.DoesNotContain(capture.Messages, message => message.Contains("private-canary", StringComparison.Ordinal));
            if (mode == "copy") Assert.True(stream.Disposed);
            if (mode is "broken" or "media") { oss.VerifyNoOtherCalls(); remote.VerifyNoOtherCalls(); }
        }
        finally { await CleanupAsync(factory, seed); }
    }

    [Fact]
    public async Task ConcurrentTreeAndBlockChange_ReturnsOneSnapshotAndNextPageConflicts()
    {
        using var seedFactory = Factory();
        var seed = await SeedAsync(seedFactory);
        var probe = new ReadProbe { PauseAt = 2 };
        using var factory = Factory(services => services.AddDbContext<DocthecaDbContext>(options => options.AddInterceptors(probe)));
        using var client = factory.CreateClient();
        try
        {
            probe.Active = true;
            var reading = client.GetStringAsync(Url(seed.Node) + "?limit=2");
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using (var scope = seedFactory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
                await db.DocumentStructureNodes.Where(item => item.Id == seed.Node).ExecuteUpdateAsync(set => set.SetProperty(item => item.Name, "new-tree")).WaitAsync(TimeSpan.FromSeconds(5));
                await db.DocumentParseBlocks.Where(item => item.Id == seed.Blocks[0]).ExecuteUpdateAsync(set => set.SetProperty(item => item.TextContent, "new-content")).WaitAsync(TimeSpan.FromSeconds(5));
            }
            probe.Resume.TrySetResult();
            using var body = JsonDocument.Parse(await reading);
            Assert.Equal("original", body.RootElement.GetProperty("data").GetProperty("items")[0].GetProperty("content").GetString());
            Assert.Equal(IsolationLevel.RepeatableRead, probe.Isolation);
            var revision = body.RootElement.GetProperty("data").GetProperty("revision").GetString();
            using var changed = await client.GetAsync(Url(seed.Node) + $"?afterSequence=3&revision={revision}");
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            Assert.True(probe.Reads >= 8);
        }
        finally { probe.Resume.TrySetResult(); await CleanupAsync(seedFactory, seed); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringDatabaseRead_IsObservedByRelationalCalls(bool image)
    {
        using var seedFactory = Factory();
        var seed = await SeedAsync(seedFactory);
        var probe = new ReadProbe { PauseAt = 2 };
        using var factory = Factory(services => services.AddDbContext<DocthecaDbContext>(options => options.AddInterceptors(probe)));
        using var client = factory.CreateClient();
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            probe.Active = true;
            var read = client.GetAsync(image ? ImageUrl(seed.Node, seed.Image, new string('0', 64)) : Url(seed.Node), cancel.Token);
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            await probe.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var scope = factory.Services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IInternalContentRepository>();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ReadContentAsync(seed.Node, new(null, 1, null), cancel.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ReadImageAsync(seed.Node, seed.Image, new string('0', 64), cancel.Token));
        }
        finally { probe.Resume.TrySetResult(); await CleanupAsync(seedFactory, seed); }
    }

    [Theory]
    [InlineData(404)]
    [InlineData(503)]
    [InlineData(0)]
    public async Task NamedRemoteClient_FailuresHaveSafeHostMappingAndNoUriOrProviderLogs(int status)
    {
        var content = new TrackingContent(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("private-canary/provider-error")));
        var handler = new AssetHandler(_ => status == 0
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("private-canary/transport"))
            : Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = content }));
        var capture = new LogCapture();
        using var factory = Factory(services =>
        {
            services.AddHttpClient("StructaDoc", client => client.BaseAddress = new Uri("http://private-canary.invalid/")).ConfigurePrimaryHttpMessageHandler(() => handler);
            services.RemoveAll<IStructaDocClient>();
            services.AddSingleton<IStructaDocClient>(provider => new StructaDocClient(provider.GetRequiredService<IHttpClientFactory>().CreateClient("StructaDoc"), Options.Create(new StructaDocOptions { ApiKey = "synthetic" }), provider.GetRequiredService<ILogger<StructaDocClient>>()));
            services.RemoveAll<ILoggerFactory>(); services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(logging => logging.AddProvider(capture)));
        });
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory, true);
        try
        {
            var revision = await RevisionAsync(client, seed.Node);
            using var response = await client.GetAsync(ImageUrl(seed.Node, seed.Image, revision));
            Assert.Equal(status == 404 ? HttpStatusCode.NotFound : HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.DoesNotContain("private-canary", await response.Content.ReadAsStringAsync() + response.Headers + response.Content.Headers);
            Assert.DoesNotContain(capture.Messages, message => message.Contains("private-canary", StringComparison.Ordinal));
            Assert.Equal(status != 0, content.Disposed);
            Assert.Equal(1, handler.Calls);
        }
        finally { await CleanupAsync(factory, seed); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdminImageSuccessAndRange_KeepExistingBinaryContract(bool remote)
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6 };
        var streams = new List<TrackedStream>();
        Stream Fresh() { var stream = new TrackedStream(bytes); streams.Add(stream); return stream; }
        var oss = new Mock<IOssService>();
        oss.Setup(service => service.DownloadAsync(It.IsAny<string>())).ReturnsAsync(Fresh);
        var handler = new AssetHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new TrackingContent(Fresh()) }));
        using var factory = Factory(services =>
        {
            services.RemoveAll<IOssService>(); services.AddSingleton(oss.Object);
            services.RemoveAll<IStructaDocClient>(); services.AddSingleton<IStructaDocClient>(new StructaDocClient(new HttpClient(handler) { BaseAddress = new Uri("http://upstream.invalid/") }, Options.Create(new StructaDocOptions()), NullLogger<StructaDocClient>.Instance));
        });
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory, remote);
        try
        {
            foreach (var range in new[] { false, true })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/document-parses/{seed.Parse}/images/{seed.Image}/content");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin"));
                if (range) request.Headers.Range = new RangeHeaderValue(1, 3);
                using var response = await client.SendAsync(request);
                Assert.Equal(range ? HttpStatusCode.PartialContent : HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
                Assert.Equal(range ? new byte[] { 2, 3, 4 } : bytes, await response.Content.ReadAsByteArrayAsync());
                if (range) Assert.Equal("bytes 1-3/6", response.Content.Headers.ContentRange!.ToString());
                Assert.False(response.Headers.Contains("Content-Security-Policy"));
                Assert.True(streams[^1].Disposed);
            }
        }
        finally { await CleanupAsync(factory, seed); }
    }

    [Fact]
    public async Task EmptyNodeAndDatabaseFailure_HaveDocumentedSafeOutcomes()
    {
        using var seedFactory = Factory();
        var seed = await SeedAsync(seedFactory);
        var capture = new LogCapture();
        var probe = new ReadProbe();
        using var factory = Factory(services =>
        {
            services.AddDbContext<DocthecaDbContext>(options => options.AddInterceptors(probe));
            services.RemoveAll<ILoggerFactory>(); services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(logging => logging.AddProvider(capture)));
        });
        using var client = factory.CreateClient();
        try
        {
            var emptyNode = Guid.NewGuid();
            using (var scope = seedFactory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
                db.DocumentStructureNodes.Add(new() { Id = emptyNode, ParseId = seed.Parse, LevelId = seed.Level, Name = "empty", StartSequence = 20, EndSequence = 21 });
                await db.SaveChangesAsync();
            }
            using var empty = JsonDocument.Parse(await client.GetStringAsync(Url(emptyNode)));
            Assert.Equal(0, empty.RootElement.GetProperty("data").GetProperty("items").GetArrayLength());
            Assert.False(empty.RootElement.GetProperty("data").GetProperty("hasMore").GetBoolean());
            probe.Active = true; probe.Fail = true;
            using var failure = await client.GetAsync(Url(seed.Node));
            Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
            Assert.Contains("http.internal_server_error", await failure.Content.ReadAsStringAsync());
            using var imageFailure = await client.GetAsync(ImageUrl(seed.Node, seed.Image, new string('0', 64)));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, imageFailure.StatusCode);
            Assert.DoesNotContain("private-canary", await imageFailure.Content.ReadAsStringAsync());
            Assert.DoesNotContain(capture.Messages, message => message.Contains("private-canary", StringComparison.Ordinal));
        }
        finally { probe.Active = false; await CleanupAsync(seedFactory, seed); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartedImageCancellationOrReadFailure_AbortsAndDisposes(bool fail)
    {
        var stream = new InterruptStream(fail);
        var oss = new Mock<IOssService>();
        oss.Setup(service => service.DownloadAsync(It.IsAny<string>())).ReturnsAsync(stream);
        var capture = new LogCapture();
        using var factory = Factory(services =>
        {
            services.RemoveAll<IOssService>(); services.AddSingleton(oss.Object);
            services.RemoveAll<ILoggerFactory>(); services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(logging => logging.AddProvider(capture)));
        });
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory);
        try
        {
            var revision = await RevisionAsync(client, seed.Node);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var read = client.GetAsync(ImageUrl(seed.Node, seed.Image, revision), HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (fail) stream.Resume.TrySetResult();
            else cancellation.Cancel();
            var error = await Record.ExceptionAsync(async () =>
            {
                using var response = await read;
                await response.Content.ReadAsByteArrayAsync(cancellation.Token);
            });
            Assert.NotNull(error);
            Assert.DoesNotContain("private-canary", error.ToString());
            if (!fail) await stream.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await stream.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(!fail, stream.Cancelled);
            Assert.DoesNotContain(capture.Messages, message => message.Contains("private-canary", StringComparison.Ordinal));
        }
        finally { stream.Resume.TrySetResult(); await CleanupAsync(factory, seed); }
    }

    private sealed class InterruptStream(bool fail) : MemoryStream
    {
        public bool Cancelled;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new byte[] { 1 }, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            Entered.TrySetResult();
            try { await Resume.Task.WaitAsync(cancellationToken); cancellationToken.ThrowIfCancellationRequested(); }
            catch (OperationCanceledException) { Cancelled = true; CancellationObserved.TrySetResult(); throw; }
            if (fail) throw new IOException("private-canary/started-copy");
        }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); Released.TrySetResult(); }
    }

    private static async Task<string> RevisionAsync(HttpClient client, Guid node)
    {
        using var body = JsonDocument.Parse(await client.GetStringAsync(Url(node)));
        return body.RootElement.GetProperty("data").GetProperty("revision").GetString()!;
    }
    private static void Keys(JsonElement item, params string[] keys) => Assert.Equal(keys.Order(), item.EnumerateObject().Select(property => property.Name).Order());
    private sealed record Seed(Guid Definition, Guid Level, Guid File, Guid Parse, Guid Node, Guid Neighbor, Guid Image, Guid NeighborImage, Guid Run, Guid Asset, Guid[] Blocks);
    private static async Task<Seed> SeedAsync(WebApplicationFactory<Program> factory, bool remote = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var ids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
        var definition = ids[0]; var level = ids[1]; var file = ids[2]; var parse = ids[3];
        db.StructureDefinitions.Add(new() { Id = definition, Name = "definition" });
        db.StructureLevels.Add(new() { Id = level, DefinitionId = definition, Name = "Section" });
        db.DocumentFiles.Add(new() { Id = file, FileName = "private-canary", FilePath = "private-canary", ContentType = "application/pdf", StructureDefinitionId = definition });
        db.DocumentParses.Add(new() { Id = parse, DocumentFileId = file, Status = DocumentParseStatus.Parsed, ParsedAt = DateTimeOffset.UtcNow, StructaDocParseRunId = remote ? ids[8] : null, ModelJson = "{\"private-canary\":1}" });
        db.DocumentStructureNodes.AddRange(new DocumentStructureNodeEntity { Id = ids[4], ParseId = parse, LevelId = level, Name = "selected", StartSequence = 2, EndSequence = 10, UpdatedAt = DateTimeOffset.UnixEpoch }, new DocumentStructureNodeEntity { Id = ids[5], ParseId = parse, LevelId = level, Name = "neighbor", StartSequence = 11, EndSequence = 12 });
        db.DocumentParseImages.AddRange(new DocumentParseImageEntity { Id = ids[6], ParseId = parse, ImagePath = remote ? ids[9].ToString() : "private-canary/key", ImageName = "private-canary", ContentType = "image/png" }, new DocumentParseImageEntity { Id = ids[7], ParseId = parse, ImagePath = "private-canary/neighbor", ContentType = "image/png" });
        var blocks = new List<Guid>();
        foreach (var sequence in new[] { 0, 2, 3, 5, 8, 10, 11 })
        {
            var block = Guid.NewGuid();
            db.DocumentParseBlocks.Add(new() { Id = block, ParseId = parse, Sequence = sequence, PageId = sequence % 3, SortIndex = sequence, BlockType = sequence == 5 ? "image" : sequence == 3 ? "table" : "text", SubType = sequence == 3 ? "table" : null, TextContent = sequence == 3 ? "<table><tr><td>untrusted text</td></tr></table>" : "original", TextFormat = sequence == 3 ? "html" : "", ImageId = sequence == 5 ? ids[6] : sequence == 11 ? ids[7] : null, BlockData = "{\"private-canary\":1}" });
            if (sequence is >= 2 and <= 10) blocks.Add(block);
        }
        db.DocumentParseBlocks.Add(new() { ParseId = parse, Sequence = null, PageId = 999, BlockType = "text", TextContent = "private-canary/unsequenced" });
        await db.SaveChangesAsync();
        return new(definition, level, file, parse, ids[4], ids[5], ids[6], ids[7], ids[8], ids[9], blocks.ToArray());
    }
    private static async Task CleanupAsync(WebApplicationFactory<Program> factory, Seed seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        await db.DocumentFiles.Where(item => item.Id == seed.File).ExecuteDeleteAsync();
        await db.StructureDefinitions.Where(item => item.Id == seed.Definition).ExecuteDeleteAsync();
    }
    private sealed class TrackedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed;
        public bool ThrowOnRead;
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            ThrowOnRead ? Task.FromException(new IOException("private-canary/copy")) : base.CopyToAsync(destination, bufferSize, cancellationToken);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class TrackingContent(Stream stream) : StreamContent(stream)
    {
        public bool Disposed;
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class AssetHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public string? Path;
        public string? Scheme;
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Path = request.RequestUri!.PathAndQuery; Scheme = request.Headers.Authorization?.Scheme;
            return handler(request);
        }
    }
    private sealed class LogCapture : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Messages);
        public void Dispose() { }
        private sealed class CaptureLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception) + exception);
        }
    }
    private sealed class ReadProbe : DbCommandInterceptor
    {
        public bool Active;
        public bool Fail;
        public int Reads;
        public int PauseAt;
        public IsolationLevel Isolation;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Active) { Assert.StartsWith("SELECT", command.CommandText.TrimStart()); Assert.DoesNotContain("FOR UPDATE", command.CommandText); if (Fail) throw new InvalidOperationException("private-canary/provider-error"); }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Active && Interlocked.Increment(ref Reads) == PauseAt)
            {
                Isolation = command.Transaction!.IsolationLevel;
                Entered.TrySetResult();
                try { await Resume.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            return result;
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Assert.False(Active, "Internal content reads must not write.");
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
