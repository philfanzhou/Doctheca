using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Database.Repositories;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Doctheca.Tests.Integration;

[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed class DocumentStructurePageLabelTests(PostgreSqlFixture database) : ServiceMantleIntegrationTestBase(database)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothViews_PreserveLabelsOldFieldsAndCursorBounds(bool node)
    {
        var probe = new PageProbe();
        using var factory = Factory(probe);
        using var client = Admin(factory);
        var seed = await SeedAsync(factory);
        var foreign = await SeedAsync(factory, label: "foreign");
        var route = Route(seed, node);
        var items = new List<JsonNode>();
        for (var after = -1; ;)
        {
            var data = await DataAsync(client, $"{route}?afterSequence={after}&limit=2");
            var page = data["items"]!.AsArray();
            items.AddRange(page.Select(item => item!));
            var hasMore = data["hasMore"]!.GetValue<bool>();
            hasMore.Should().Be(items.Count < 8);
            if (!hasMore)
            {
                data["nextAfterSequence"].Should().BeNull();
                break;
            }
            after = data["nextAfterSequence"]!.GetValue<int>();
            after.Should().Be(items.Count - 1);
        }

        string?[] labels = ["封面\niv", "封面\niv", "封面\niv", null, "", null, null, "last"];
        int[] physicalPages = [1, 2, 1, 1, 3, 4, 5, 6];
        for (var i = 0; i < items.Count; i++)
        {
            var expected = new JsonObject
            {
                ["id"] = seed.Blocks[i].ToString(), ["sequence"] = i, ["pageNumber"] = physicalPages[i],
                ["pageLabel"] = labels[i], ["blockType"] = "text", ["subType"] = "body",
                ["content"] = $"synthetic block {i}", ["contentFormat"] = i == 4 ? null : "plain",
                ["imageId"] = i == 0 ? seed.Image.ToString() : null,
                ["imageUrl"] = i == 0 ? $"/admin/document-parses/{seed.Parse}/images/{seed.Image}/content" : null,
            };
            JsonNode.DeepEquals(items[i], expected).Should().BeTrue($"all old fields and the label of sequence {i} are exact");
        }
        probe.Reads.Select(keys => keys.Select(key => key.PageNumber).Order().ToArray())
            .Should().BeEquivalentTo(new[] { new[] { 1, 2 }, new[] { 1 }, new[] { 3, 4 }, new[] { 5, 6 } }, options => options.WithStrictOrdering());
        probe.Reads.SelectMany(keys => keys).Should().OnlyContain(key => key.ParseId == seed.Parse);
        probe.Writes.Should().Be(0);

        // A different parse's identical physical page numbers cannot borrow this parse's labels.
        (await DataAsync(client, Route(foreign, node) + "?limit=1"))["items"]![0]!["pageLabel"]!.GetValue<string>()
            .Should().Be("foreign");
        var empty = await DataAsync(client, route + "?afterSequence=100&limit=2");
        empty["items"]!.AsArray().Should().BeEmpty();
        empty["hasMore"]!.GetValue<bool>().Should().BeFalse();
        probe.Reads.Should().HaveCount(5, "an empty cursor page needs no page lookup");
        // Existing clamping remains in force.
        (await DataAsync(client, route + "?limit=0"))["items"]!.AsArray().Should().HaveCount(1);
        (await DataAsync(client, route + "?limit=1001"))["items"]!.AsArray().Should().HaveCount(8);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingPagesAndLegacyProvenance_ReturnNullWithoutMutation(bool node)
    {
        var probe = new PageProbe();
        using var factory = Factory(probe);
        using var client = Admin(factory);
        var missingPages = await SeedAsync(factory, pages: false);
        var legacy = await SeedAsync(factory, legacy: true);
        foreach (var seed in new[] { missingPages, legacy })
        {
            var items = (await DataAsync(client, Route(seed, node)))["items"]!.AsArray();
            items.Should().HaveCount(8);
            items.Should().OnlyContain(item => item!.AsObject().ContainsKey("pageLabel") && item["pageLabel"] == null);
        }
        probe.Reads.Should().ContainSingle("legacy provenance never qualifies for any lookup");
        probe.Writes.Should().Be(0);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        (await db.DocumentParsePages.CountAsync(page => page.ParseId == legacy.Parse)).Should().Be(5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedLookup_PreservesBlocksAndEmitsOneContentFreeWarning(bool node)
    {
        var probe = new PageProbe { BeforeRead = _ => throw new InvalidOperationException("provider-private-canary") };
        using var factory = Factory(probe);
        using var client = Admin(factory);
        var seed = await SeedAsync(factory);
        var data = await DataAsync(client, Route(seed, node) + "?limit=2");
        data["items"]!.AsArray().Select(item => item!["sequence"]!.GetValue<int>()).Should().Equal(0, 1);
        data["items"]!.AsArray().Should().OnlyContain(item => item!["pageLabel"] == null);
        data["hasMore"]!.GetValue<bool>().Should().BeTrue();
        data["nextAfterSequence"]!.GetValue<int>().Should().Be(1);
        probe.Messages.Should().Equal((LogLevel.Warning, "Optional ordered-block page labels unavailable", (Exception?)null));
        probe.Writes.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellation_ReachesLookupAndNeverBecomesOptionalSuccess(bool node)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new PageProbe
        {
            BeforeRead = async ct =>
            {
                ct.CanBeCanceled.Should().BeTrue();
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            },
        };
        using var factory = Factory(probe);
        using var client = Admin(factory);
        var seed = await SeedAsync(factory);
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetAsync(Route(seed, node), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        probe.Messages.Should().BeEmpty("cancellation is not a page-availability failure");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentReplacementAndForeignRead_DoNotMixCommittedPageSets(bool node)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        var probe = new PageProbe
        {
            AfterRead = async ct =>
            {
                if (Interlocked.Increment(ref first) != 1) return;
                reached.TrySetResult();
                await resume.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            },
        };
        using var factory = Factory(probe);
        using var client = Admin(factory);
        var seed = await SeedAsync(factory, label: "old");
        var foreign = await SeedAsync(factory, label: "foreign");
        var pending = DataAsync(client, Route(seed, node) + "?limit=2");
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var scope = factory.Services.CreateScope();
            await new DocumentParsePageRepository(scope.ServiceProvider.GetRequiredService<DocthecaDbContext>())
                .ReplaceAsync(seed.Parse, [new(seed.Parse, 1, "new", null), new(seed.Parse, 2, "new", null)]);
            var other = await DataAsync(client, Route(foreign, node) + "?limit=2");
            other["items"]!.AsArray().Select(item => item!["pageLabel"]!.GetValue<string>()).Should().Equal("foreign", "foreign");
        }
        finally { resume.TrySetResult(); }
        (await pending)["items"]!.AsArray().Select(item => item!["pageLabel"]!.GetValue<string>()).Should().Equal("old", "old");
        (await DataAsync(client, Route(seed, node) + "?limit=2"))["items"]!.AsArray()
            .Select(item => item!["pageLabel"]!.GetValue<string>()).Should().Equal("new", "new");
        probe.Messages.Should().BeEmpty();
        probe.Writes.Should().Be(0);
    }

    private WebApplicationFactory<Program> Factory(PageProbe probe) => CreateFactory(configureTestServices: services =>
    {
        services.RemoveAll<IDocumentParsePageRepository>();
        services.AddScoped<IDocumentParsePageRepository>(provider =>
            new ObservedPages(new DocumentParsePageRepository(provider.GetRequiredService<DocthecaDbContext>()), probe));
        services.RemoveAll<ILoggerFactory>();
        services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(builder => builder.AddProvider(probe)));
    });

    private static HttpClient Admin(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin"));
        return client;
    }

    private static string Route(Seed seed, bool node) => node
        ? $"/admin/document-parses/{seed.Parse}/structure/nodes/{seed.Node}/blocks"
        : $"/admin/document-parses/{seed.Parse}/blocks";

    private static async Task<JsonNode> DataAsync(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["success"]!.GetValue<bool>().Should().BeTrue();
        return body["data"]!;
    }

    private sealed record Seed(Guid Parse, Guid Node, Guid Image, Guid[] Blocks);

    private static async Task<Seed> SeedAsync(WebApplicationFactory<Program> factory, string label = "封面\niv", bool pages = true, bool legacy = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var definition = new StructureDefinitionEntity { Name = "Synthetic definition" };
        var level = new StructureLevelEntity { DefinitionId = definition.Id, Name = "Synthetic level" };
        var file = new DocumentFileEntity { FileName = "synthetic.pdf", ContentType = "application/pdf", StructureDefinitionId = definition.Id };
        var parse = new DocumentParseEntity { DocumentFileId = file.Id, Status = "parsed", StructaDocParseRunId = legacy ? null : Guid.NewGuid() };
        var node = new DocumentStructureNodeEntity { ParseId = parse.Id, LevelId = level.Id, Name = "Synthetic node", StartSequence = 0, EndSequence = 7 };
        var image = new DocumentParseImageEntity { ParseId = parse.Id, ImageName = "synthetic.png", ImagePath = Guid.NewGuid().ToString() };
        int?[] sourcePages = [1, 2, 1, null, 3, 4, 5, 6];
        var blocks = sourcePages.Select((page, i) => new DocumentParseBlockEntity
        {
            ParseId = parse.Id, Sequence = i, PageId = (page ?? 1) - 1, SortIndex = i,
            BlockType = "text", SubType = "body", TextContent = $"synthetic block {i}",
            TextFormat = i == 4 ? "" : "plain", ImageId = i == 0 && !legacy ? image.Id : null,
            BlockData = new JsonObject { ["pageNumber"] = page, ["sequence"] = i }.ToJsonString(),
        }).ToArray();
        db.AddRange(definition, level, file, parse, node, image);
        db.DocumentParseBlocks.AddRange(blocks);
        await db.SaveOwnedChangesAsync();
        if (pages) await new DocumentParsePageRepository(db).ReplaceAsync(parse.Id,
            [new(parse.Id, 1, label, null), new(parse.Id, 2, label, null), new(parse.Id, 3, "", null),
                new(parse.Id, 4, null, null), new(parse.Id, 6, "last", null)]);
        return new(parse.Id, node.Id, image.Id, blocks.Select(block => block.Id).ToArray());
    }

    private sealed class PageProbe : ILoggerProvider
    {
        public ConcurrentQueue<DocumentParsePageKey[]> Reads { get; } = new();
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Messages { get; } = new();
        public int Writes;
        public Func<CancellationToken, Task>? BeforeRead;
        public Func<CancellationToken, Task>? AfterRead;
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void Dispose() { }
        private sealed class Logger(PageProbe probe, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category == "DocumentStructureEndpoints") probe.Messages.Enqueue((logLevel, formatter(state, exception), exception));
            }
        }
    }

    private sealed class ObservedPages(IDocumentParsePageRepository inner, PageProbe probe) : IDocumentParsePageRepository
    {
        public Task ReplaceAsync(Guid parseId, IReadOnlyCollection<DocumentParsePageModel> pages, CancellationToken ct = default)
        {
            Interlocked.Increment(ref probe.Writes);
            return inner.ReplaceAsync(parseId, pages, ct);
        }
        public async Task<IReadOnlyDictionary<DocumentParsePageKey, DocumentParsePageModel>> ReadBatchAsync(
            IReadOnlyCollection<DocumentParsePageKey> keys, CancellationToken ct = default)
        {
            probe.Reads.Enqueue(keys.ToArray());
            if (probe.BeforeRead != null) await probe.BeforeRead(ct);
            var result = await inner.ReadBatchAsync(keys, ct);
            if (probe.AfterRead != null) await probe.AfterRead(ct);
            return result;
        }
    }
}
