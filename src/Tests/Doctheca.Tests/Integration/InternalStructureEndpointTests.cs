using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Text.Json;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Database.Repositories;
using Doctheca.Domain.Models;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services.Structure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Doctheca.Tests.Integration;

[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed class InternalStructureEndpointTests(PostgreSqlFixture database) : ServiceMantleIntegrationTestBase(database)
{
    private static string Url(Guid file) => $"/internal/v1/documents/{file}/structure";
    private WebApplicationFactory<Program> Factory(Action<IServiceCollection>? services = null) =>
        CreateFactory(configureTestServices: services, settings: new Dictionary<string, string?>());

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Tree_UsesStoredLevelsPathsAndPhysicalRangesWithOnlyAllowedFields(int depth)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory, depth);
        try
        {
            using var response = await client.GetAsync(Url(seed.File));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("canary", text);
            using var body = JsonDocument.Parse(text);
            AssertKeys(body.RootElement, "success", "data");
            Assert.True(body.RootElement.GetProperty("success").GetBoolean());
            var data = body.RootElement.GetProperty("data");
            AssertKeys(data, "documentFileId", "parseId", "nodes");
            Assert.Equal(seed.File, data.GetProperty("documentFileId").GetGuid());
            Assert.Equal(seed.Parse, data.GetProperty("parseId").GetGuid());
            var roots = data.GetProperty("nodes").EnumerateArray().ToArray();
            Assert.Equal(new[] { "B", "A", "Empty" }, roots.Select(node => node.GetProperty("name").GetString()));
            var nodes = Flatten(roots).ToDictionary(node => node.GetProperty("id").GetGuid());
            Assert.Equal(seed.Nodes.Count, nodes.Count);
            foreach (var expected in seed.Nodes)
            {
                var actual = nodes[expected.Id];
                AssertKeys(actual, "id", "levelId", "levelName", "name", "path", "startSequence", "endSequence", "startPageNumber", "endPageNumber", "children", "audioCount");
                Assert.Equal(expected.LevelId, actual.GetProperty("levelId").GetGuid());
                Assert.Equal("Layer " + Array.IndexOf(seed.Levels, expected.LevelId), actual.GetProperty("levelName").GetString());
                Assert.Equal(expected.Name, actual.GetProperty("name").GetString());
                Assert.Equal(expected.StartSequence, actual.GetProperty("startSequence").GetInt32());
                Assert.Equal(expected.EndSequence, actual.GetProperty("endSequence").GetInt32());
                Assert.Equal(0, actual.GetProperty("audioCount").GetInt32());
            }
            Assert.Equal(new[] { "A1", "A2" }, roots[1].GetProperty("children").EnumerateArray().Select(node => node.GetProperty("name").GetString()));
            Assert.Equal("A > A1", nodes[seed.Nodes[3].Id].GetProperty("path").GetString());
            Assert.Equal(1, roots[1].GetProperty("startPageNumber").GetInt32());
            Assert.Equal(5, roots[1].GetProperty("endPageNumber").GetInt32());
            Assert.Equal(JsonValueKind.Null, roots[2].GetProperty("startPageNumber").ValueKind);
            Assert.Equal(JsonValueKind.Null, roots[2].GetProperty("endPageNumber").ValueKind);
            if (depth == 3)
            {
                var leaf = nodes[seed.Nodes[^1].Id];
                Assert.Equal("A > A1 > Leaf", leaf.GetProperty("path").GetString());
                Assert.Equal(1, leaf.GetProperty("startPageNumber").GetInt32());
                Assert.Equal(1, leaf.GetProperty("endPageNumber").GetInt32());
            }
        }
        finally { await CleanupAsync(factory, seed); }
    }

    [Fact]
    public async Task MaximumDefinitionDepth_ReturnsTheCompleteValidatedTree()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory, 50);
        try
        {
            IReadOnlyList<StructureNodeInput> inputs = [];
            for (var i = 49; i >= 0; i--) inputs = [new(null, seed.Levels[i], "N" + i, 0, 3, inputs)];
            using (var scope = factory.Services.CreateScope())
            {
                var service = scope.ServiceProvider.GetRequiredService<IDocumentStructureService>();
                var saved = await service.SaveTreeAsync(seed.Parse, inputs);
                Assert.Equal(StructureResultStatus.Ok, saved.Status);
                Assert.Equal(50, saved.Value!.Nodes.Count);
            }
            using var response = await client.GetAsync(Url(seed.File));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(), new JsonDocumentOptions { MaxDepth = 128 });
            var nodes = Flatten(body.RootElement.GetProperty("data").GetProperty("nodes").EnumerateArray()).ToArray();
            Assert.Equal(50, nodes.Length);
            Assert.Equal(string.Join(" > ", Enumerable.Range(0, 50).Select(i => "N" + i)), nodes[^1].GetProperty("path").GetString());
            Assert.Equal("Layer 49", nodes[^1].GetProperty("levelName").GetString());
        }
        finally { await CleanupAsync(factory, seed); }
    }

    [Fact]
    public async Task Selection_UsesOnlyParsedNonemptyTreesThenTimestampNullsLastAndUuidDescending()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory);
        try
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
            var now = DateTimeOffset.UtcNow;
            await db.DocumentParses.Where(item => item.Id == seed.Parse).ExecuteUpdateAsync(set => set.SetProperty(item => item.ParsedAt, now.AddDays(-1)));
            var ids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).Order().ToArray();
            var counts = new Dictionary<Guid, int> { [seed.Parse] = 0 };
            async Task Add(Guid id, string status, DateTimeOffset? parsedAt, bool marked)
            {
                db.DocumentParses.Add(new DocumentParseEntity { Id = id, DocumentFileId = seed.File, Status = status, ParsedAt = parsedAt });
                if (marked)
                {
                    var node = new DocumentStructureNodeEntity { ParseId = id, LevelId = seed.Levels[0], Name = "alternate", StartSequence = 0, EndSequence = 1 };
                    db.DocumentStructureNodes.Add(node);
                    counts[id] = Array.IndexOf(ids, id) + 1;
                    db.DocumentStructureAudio.AddRange(Enumerable.Range(0, counts[id]).Select(_ => new DocumentStructureAudioEntity
                    { ParseId = id, NodeId = node.Id, SourcePath = Guid.NewGuid() + ".mp3", ObjectKey = "audio-key-canary-" + Guid.NewGuid(), ContentType = "audio/mpeg", MatchStatus = "matched" }));
                }
                else counts[id] = 0;
                await db.SaveChangesAsync();
            }
            async Task AssertSelected(Guid expected)
            {
                await AssertCurrentAsync(client, seed.File, expected);
                using var tree = JsonDocument.Parse(await client.GetStringAsync(Url(seed.File)));
                Assert.Equal(counts[expected], Flatten(tree.RootElement.GetProperty("data").GetProperty("nodes").EnumerateArray())
                    .Sum(node => node.GetProperty("audioCount").GetInt32()));
                using var catalog = JsonDocument.Parse(await client.GetStringAsync($"/internal/v1/documents?structureDefinitionId={seed.Definition}"));
                Assert.Equal(counts[expected], Assert.Single(catalog.RootElement.GetProperty("data").GetProperty("items").EnumerateArray()).GetProperty("audioCount").GetInt32());
            }
            await Add(ids[0], DocumentParseStatus.Parsed, now, true);
            await Add(ids[1], DocumentParseStatus.Parsed, now, true);
            await Add(ids[9], DocumentParseStatus.Parsed, null, true);
            await Add(ids[8], DocumentParseStatus.Pending, now.AddDays(1), true);
            await Add(ids[7], DocumentParseStatus.Parsing, now.AddDays(1), true);
            await Add(ids[6], DocumentParseStatus.Failed, now.AddDays(1), true);
            await Add(ids[5], DocumentParseStatus.Parsed, now.AddDays(1), false);
            await AssertSelected(ids[1]);
            // UUID alone cannot hide an older timestamp, or promote a null timestamp over a real one.
            await db.DocumentParses.Where(item => item.Id == ids[9]).ExecuteUpdateAsync(set => set.SetProperty(item => item.ParsedAt, now.AddDays(-2)));
            await AssertSelected(ids[1]);
            await db.DocumentParses.Where(item => item.Id == ids[9]).ExecuteUpdateAsync(set => set.SetProperty(item => item.ParsedAt, now.AddMinutes(1)));
            await AssertSelected(ids[9]);
            // A newly marked parsed parse becomes current on the next request.
            db.DocumentStructureNodes.Add(new DocumentStructureNodeEntity { ParseId = ids[5], LevelId = seed.Levels[0], Name = "promoted", StartSequence = 0, EndSequence = 1 });
            await db.SaveChangesAsync();
            await AssertSelected(ids[5]);
            await db.DocumentParses.Where(item => item.DocumentFileId == seed.File).ExecuteUpdateAsync(set => set.SetProperty(item => item.ParsedAt, (DateTimeOffset?)null));
            await AssertSelected(new[] { seed.Parse, ids[0], ids[1], ids[5], ids[9] }.Max());
        }
        finally { await CleanupAsync(factory, seed); }
    }

    [Fact]
    public async Task MissingUnmarkedAndInvalidRequests_HaveDistinctFixedSafeResults()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory);
        try
        {
            using var absent = await client.GetAsync(Url(Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
            Assert.Equal("{\"success\":false,\"errorCode\":\"DOCTHECA_INTERNAL_DOCUMENT_NOT_FOUND\",\"message\":\"Document not found.\"}", await absent.Content.ReadAsStringAsync());
            using (var scope = factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<DocthecaDbContext>().DocumentStructureNodes.Where(item => item.ParseId == seed.Parse).ExecuteDeleteAsync();
            using var unmarked = await client.GetAsync(Url(seed.File));
            Assert.Equal(HttpStatusCode.NotFound, unmarked.StatusCode);
            Assert.Equal("{\"success\":false,\"errorCode\":\"DOCTHECA_INTERNAL_STRUCTURE_NOT_FOUND\",\"message\":\"Document has no marked structure.\"}", await unmarked.Content.ReadAsStringAsync());
            foreach (var path in new[] { "/internal/v1/documents/bad-canary/structure", $"/internal/v1/documents/{seed.File:N}/structure", Url(seed.File) + "?unknown=input-canary" })
            {
                using var invalid = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                Assert.Equal("{\"success\":false,\"errorCode\":\"DOCTHECA_INTERNAL_INVALID\",\"message\":\"Invalid internal API request.\"}", await invalid.Content.ReadAsStringAsync());
            }
        }
        finally { await CleanupAsync(factory, seed); }
    }

    [Fact]
    public async Task NodeLookup_RejectsAbsentAndStaleNodesAndResolvesEachDocumentsOwnCurrentParse()
    {
        using var factory = Factory();
        var seed = await SeedAsync(factory);
        var other = await SeedAsync(factory);
        try
        {
            using var scope = factory.Services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IInternalStructureRepository>();
            var node = await repository.ReadCurrentNodeAsync(seed.Nodes[0].Id, default);
            Assert.Equal(new InternalCurrentStructureNode(seed.File, seed.Parse, seed.Nodes[0].Id, 0, 3), node);
            var foreign = await repository.ReadCurrentNodeAsync(other.Nodes[0].Id, default);
            Assert.Equal(other.File, foreign!.DocumentFileId);
            Assert.Equal(other.Parse, foreign.ParseId);
            Assert.Null(await repository.ReadCurrentNodeAsync(Guid.NewGuid(), default));
            var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
            var newParse = Guid.NewGuid();
            var newNode = Guid.NewGuid();
            db.DocumentParses.Add(new DocumentParseEntity { Id = newParse, DocumentFileId = seed.File, Status = DocumentParseStatus.Parsed, ParsedAt = DateTimeOffset.UtcNow.AddDays(1) });
            db.DocumentStructureNodes.Add(new DocumentStructureNodeEntity { Id = newNode, ParseId = newParse, LevelId = seed.Levels[0], Name = "new", StartSequence = 2, EndSequence = 4 });
            await db.SaveChangesAsync();
            Assert.Null(await repository.ReadCurrentNodeAsync(seed.Nodes[0].Id, default));
            Assert.Equal(new InternalCurrentStructureNode(seed.File, newParse, newNode, 2, 4), await repository.ReadCurrentNodeAsync(newNode, default));
            await db.DocumentParses.Where(item => item.Id == newParse).ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, DocumentParseStatus.Failed));
            Assert.Null(await repository.ReadCurrentNodeAsync(newNode, default));
            Assert.NotNull(await repository.ReadCurrentNodeAsync(seed.Nodes[0].Id, default));
        }
        finally { await CleanupAsync(factory, seed); await CleanupAsync(factory, other); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentReplacementOrDeletion_ReturnsOneWholeSnapshotWithoutBlockingWriter(bool delete)
    {
        using var seedFactory = Factory();
        var seed = await SeedAsync(seedFactory);
        var pause = new ReadProbe { PauseAt = 2 };
        using var factory = Factory(services => services.AddDbContext<DocthecaDbContext>(options => options.AddInterceptors(pause)));
        using var client = factory.CreateClient();
        try
        {
            using (var scope = seedFactory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
                db.DocumentStructureAudio.Add(new() { ParseId = seed.Parse, NodeId = seed.Nodes[0].Id,
                    SourcePath = "snapshot.mp3", ObjectKey = "audio-key-canary-" + Guid.NewGuid(), ContentType = "audio/mpeg", MatchStatus = "matched" });
                await db.SaveChangesAsync();
            }
            pause.Active = true;
            var reading = client.GetStringAsync(Url(seed.File));
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The reader has selected its parse. A real writer may replace/delete it while
            // the read is paused; no parse-row update lock should be taken by the reader.
            using (var scope = seedFactory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
                if (delete) await db.DocumentFiles.Where(item => item.Id == seed.File).ExecuteDeleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
                else
                {
                    var repository = scope.ServiceProvider.GetRequiredService<IDocumentStructureRepository>();
                    Assert.True(await repository.ReplaceAsync(seed.Parse,
                        [new DocumentStructureNodeModel { Id = Guid.NewGuid(), ParseId = seed.Parse, LevelId = seed.Levels[0], Name = "replacement", StartSequence = 0, EndSequence = 3 }]).WaitAsync(TimeSpan.FromSeconds(5)));
                    await db.StructureLevels.Where(item => item.DefinitionId == seed.Definition).ExecuteUpdateAsync(set => set.SetProperty(item => item.Name, "renamed-level"));
                    await db.DocumentParseBlocks.Where(item => item.ParseId == seed.Parse).ExecuteUpdateAsync(set => set.SetProperty(item => item.PageId, 99));
                }
            }
            pause.Resume.TrySetResult();
            using var body = JsonDocument.Parse(await reading);
            Assert.Equal(seed.Parse, body.RootElement.GetProperty("data").GetProperty("parseId").GetGuid());
            var nodes = Flatten(body.RootElement.GetProperty("data").GetProperty("nodes").EnumerateArray()).ToArray();
            Assert.Equal(seed.Nodes.Select(item => item.Id).Order(), nodes.Select(item => item.GetProperty("id").GetGuid()).Order());
            Assert.All(nodes, node => Assert.StartsWith("Layer ", node.GetProperty("levelName").GetString()));
            var rootA = Assert.Single(nodes, node => node.GetProperty("name").GetString() == "A");
            Assert.Equal(5, rootA.GetProperty("endPageNumber").GetInt32());
            Assert.Equal(1, rootA.GetProperty("audioCount").GetInt32());
            Assert.Equal(IsolationLevel.RepeatableRead, pause.Isolation);
            using var next = await client.GetAsync(Url(seed.File));
            Assert.Equal(delete ? HttpStatusCode.NotFound : HttpStatusCode.OK, next.StatusCode);
            var nextText = await next.Content.ReadAsStringAsync();
            Assert.Contains(delete ? "DOCTHECA_INTERNAL_DOCUMENT_NOT_FOUND" : "replacement", nextText);
            if (!delete)
            {
                Assert.Contains("100", nextText);
                using var nextBody = JsonDocument.Parse(nextText);
                Assert.Equal(0, Assert.Single(nextBody.RootElement.GetProperty("data").GetProperty("nodes").EnumerateArray()).GetProperty("audioCount").GetInt32());
            }
        }
        finally { pause.Resume.TrySetResult(); await CleanupAsync(seedFactory, seed); }
    }

    [Fact]
    public async Task ReadsIssueOnlySelects_AndUseDeterministicUuidSiblingTies()
    {
        using var seedFactory = Factory();
        var seed = await SeedAsync(seedFactory);
        try
        {
            var ids = new[] { Guid.NewGuid(), Guid.NewGuid() }.Order().ToArray();
            using (var scope = seedFactory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
                db.DocumentStructureNodes.AddRange(ids.Select(id => new DocumentStructureNodeEntity { Id = id, ParseId = seed.Parse, LevelId = seed.Levels[0], Name = "tied", SortOrder = 10, StartSequence = 10, EndSequence = 10 }));
                await db.SaveChangesAsync();
            }
            var probe = new ReadProbe();
            using var factory = Factory(services => services.AddDbContext<DocthecaDbContext>(options => options.AddInterceptors(probe)));
            using var client = factory.CreateClient();
            probe.Active = true;
            await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            {
                using var response = await client.GetAsync(Url(seed.File));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(ids, body.RootElement.GetProperty("data").GetProperty("nodes").EnumerateArray()
                    .Where(node => node.GetProperty("name").GetString() == "tied").Select(node => node.GetProperty("id").GetGuid()));
            }));
            using var readScope = factory.Services.CreateScope();
            Assert.NotNull(await readScope.ServiceProvider.GetRequiredService<IInternalStructureRepository>().ReadCurrentNodeAsync(seed.Nodes[0].Id, default));
            Assert.True(probe.Reads >= 18);
        }
        finally { await CleanupAsync(seedFactory, seed); }
    }

    [Fact]
    public async Task RelationalFailureAndCancellation_LeakNoProviderDetailsAndNeverSucceed()
    {
        var probe = new ReadProbe { Fail = true };
        var capture = new LogCapture();
        using var factory = Factory(services =>
        {
            services.AddDbContext<DocthecaDbContext>(options => options.AddInterceptors(probe));
            services.RemoveAll<ILoggerFactory>();
            services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(logging => logging.AddProvider(capture)));
        });
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory);
        try
        {
            probe.Active = true;
            using var response = await client.GetAsync(Url(seed.File));
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.Contains("http.internal_server_error", text);
            Assert.DoesNotContain("canary", text);
            Assert.DoesNotContain(capture.Messages, log => log.Contains("provider-detail-canary", StringComparison.Ordinal));
            probe.Active = false;
            var pause = new ReadProbe { PauseAt = 2 };
            using var cancelFactory = Factory(services => services.AddDbContext<DocthecaDbContext>(options => options.AddInterceptors(pause)));
            using var cancelClient = cancelFactory.CreateClient();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            pause.Active = true;
            var reading = cancelClient.GetAsync(Url(seed.File), cancellation.Token);
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
            await pause.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var scope = cancelFactory.Services.CreateScope();
            var real = scope.ServiceProvider.GetRequiredService<IInternalStructureRepository>();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => real.ReadTreeAsync(seed.File, cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => real.ReadCurrentNodeAsync(seed.Nodes[0].Id, cancellation.Token));
            using var completed = await client.GetAsync(Url(seed.File));
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        }
        finally { probe.Active = false; await CleanupAsync(factory, seed); }
    }

    [Fact]
    public async Task CancellationAfterRepositoryReturn_IsNotSuccessful()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new Mock<IInternalStructureRepository>();
        repository.Setup(item => item.ReadTreeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid id, CancellationToken token) =>
            {
                entered.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { released.SetResult(); }
                return new InternalStructureRead(InternalStructureReadStatus.Found, new(id, Guid.NewGuid(), []));
            });
        using var factory = Factory(services => { services.RemoveAll<IInternalStructureRepository>(); services.AddSingleton(repository.Object); });
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reading = client.GetAsync(Url(Guid.NewGuid()), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task AssertCurrentAsync(HttpClient client, Guid file, Guid parse)
    {
        using var body = JsonDocument.Parse(await client.GetStringAsync(Url(file)));
        Assert.Equal(parse, body.RootElement.GetProperty("data").GetProperty("parseId").GetGuid());
    }

    private static IEnumerable<JsonElement> Flatten(IEnumerable<JsonElement> roots)
    {
        foreach (var node in roots)
        {
            yield return node;
            foreach (var child in Flatten(node.GetProperty("children").EnumerateArray())) yield return child;
        }
    }

    private static void AssertKeys(JsonElement item, params string[] keys) =>
        Assert.Equal(keys.Order(), item.EnumerateObject().Select(property => property.Name).Order());

    private sealed record Seed(Guid Definition, Guid[] Levels, Guid File, Guid Parse, List<DocumentStructureNodeEntity> Nodes);
    private static async Task<Seed> SeedAsync(WebApplicationFactory<Program> factory, int depth = 3)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var definition = Guid.NewGuid();
        var levels = Enumerable.Range(0, depth).Select(_ => Guid.NewGuid()).ToArray();
        db.StructureDefinitions.Add(new StructureDefinitionEntity { Id = definition, Name = "definition" });
        for (var i = 0; i < depth; i++) db.StructureLevels.Add(new StructureLevelEntity { Id = levels[i], DefinitionId = definition, Name = "Layer " + i, ParentLevelId = i == 0 ? null : levels[i - 1] });
        var file = Guid.NewGuid();
        var parse = Guid.NewGuid();
        db.DocumentFiles.Add(new DocumentFileEntity { Id = file, FileName = "filename-admin-canary", FilePath = "storage-path-canary", ContentType = "application/pdf", CreatedBy = Guid.NewGuid(), StructaDocDocumentId = Guid.NewGuid(), StructureDefinitionId = definition });
        db.DocumentParses.Add(new DocumentParseEntity { Id = parse, DocumentFileId = file, Status = DocumentParseStatus.Parsed, ParsedAt = DateTimeOffset.UtcNow, ZipPath = "storage-path-canary", ModelJson = "{\"raw\":\"provider-canary\"}", ErrorMessage = "error-canary" });
        var rootA = Guid.NewGuid();
        var childA = Guid.NewGuid();
        DocumentStructureNodeEntity Node(Guid id, Guid level, Guid? parent, string name, int order, int start, int end) =>
            new() { Id = id, ParseId = parse, LevelId = level, ParentId = parent, Name = name, SortOrder = order, StartSequence = start, EndSequence = end, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var nodes = new List<DocumentStructureNodeEntity>
        {
            Node(rootA, levels[0], null, "A", 1, 0, 3),
            Node(Guid.NewGuid(), levels[0], null, "B", 0, 4, 7),
            Node(Guid.NewGuid(), levels[0], null, "Empty", 2, 8, 9),
            Node(childA, levels[1], rootA, "A1", 0, 0, 1),
            Node(Guid.NewGuid(), levels[1], rootA, "A2", 0, 2, 3),
        };
        if (depth == 3) nodes.Add(Node(Guid.NewGuid(), levels[2], childA, "Leaf", 0, 1, 1));
        db.DocumentStructureNodes.AddRange(nodes);
        var pages = new[] { 2, 0, 4, 1, 5, 6, 6, 7 };
        for (var i = 0; i < pages.Length; i++) db.DocumentParseBlocks.Add(new DocumentParseBlockEntity { ParseId = parse, Sequence = i, PageId = pages[i], SortIndex = i, BlockType = "text", TextContent = "content-canary", BlockData = "{\"page_label\":\"printed-page-canary\"}" });
        db.DocumentParseBlocks.Add(new DocumentParseBlockEntity { ParseId = parse, Sequence = null, PageId = 999, BlockType = "text", BlockData = "{}" });
        await db.SaveChangesAsync();
        return new(definition, levels, file, parse, nodes);
    }

    private static async Task CleanupAsync(WebApplicationFactory<Program> factory, Seed seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        await db.DocumentFiles.Where(item => item.Id == seed.File).ExecuteDeleteAsync();
        await db.StructureLevels.Where(item => item.DefinitionId == seed.Definition).ExecuteUpdateAsync(set => set.SetProperty(item => item.ParentLevelId, (Guid?)null));
        await db.StructureDefinitions.Where(item => item.Id == seed.Definition).ExecuteDeleteAsync();
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
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception) + exception?.ToString());
        }
    }

    private sealed class ReadProbe : DbCommandInterceptor
    {
        public bool Active;
        public bool Fail;
        public int PauseAt;
        public int Reads;
        public IsolationLevel Isolation;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Active)
            {
                Assert.StartsWith("SELECT", command.CommandText.TrimStart());
                Assert.DoesNotContain("FOR UPDATE", command.CommandText);
                if (Fail) throw new InvalidOperationException("provider-detail-canary");
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Active && Interlocked.Increment(ref Reads) == PauseAt)
            {
                Isolation = command.Transaction!.IsolationLevel;
                Entered.SetResult();
                try { await Resume.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            return result;
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Assert.False(Active, "Internal reads must not issue writes.");
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
