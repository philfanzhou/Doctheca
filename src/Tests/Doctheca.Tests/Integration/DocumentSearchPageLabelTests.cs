using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Database.Repositories;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;
using Doctheca.Service;
using Doctheca.Tests.OpenSearch;
using FluentAssertions;
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
public sealed class DocumentSearchPageLabelTests(PostgreSqlFixture database) : ServiceMantleIntegrationTestBase(database)
{
    [Fact]
    public async Task RealEngineAndPostgreSql_KeepIdentityOldFieldsFilteringPagingAndFailureOutcome()
    {
        var engine = new OpenSearchFixture();
        await engine.InitializeAsync();
        try
        {
            var name = "search-labels-" + Guid.NewGuid().ToString("N");
            var probe = new ReadProbe();
            var logs = new LogCapture();
            using var factory = Factory(probe, logs, services =>
            {
                services.RemoveAll<ISearchIndexService>();
                services.AddSingleton<ISearchIndexService, OpenSearchIndexService>();
            }, new Dictionary<string, string?> { ["OpenSearch:Url"] = engine.Url, ["OpenSearch:IndexName"] = name });
            using var client = Admin(factory);
            var first = await SeedAsync(factory, "封面\niv");
            var other = await SeedAsync(factory, "別冊\n一");
            var legacy = await SeedAsync(factory, "must not leak", legacy: true);
            var noPages = await SeedAsync(factory, "missing", pages: false);
            var deleted = await SeedAsync(factory, "deleted");
            var index = factory.Services.GetRequiredService<ISearchIndexService>();
            foreach (var seed in new[] { first, other, legacy, noPages, deleted })
                (await index.IndexParseBlocksAsync(seed.Parse, seed.File, "same.pdf", "test", null, null)).Should().BeTrue();
            using (var scope = factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<DocthecaDbContext>().DocumentParses
                    .Where(parse => parse.Id == deleted.Parse).ExecuteDeleteAsync();
            using var http = new HttpClient();
            // Stale/malformed identifiers and a valid identifier with a mismatched indexed
            // page must never borrow the real first page. Indexed JSON is not provenance.
            (string? Id, int Page)[] stale = [(first.Blocks[0].ToString(), 99), (Guid.NewGuid().ToString(), 0),
                ("not-a-local-block", 0), (null, 0), (Guid.Empty.ToString(), 0)];
            foreach (var hit in stale)
            {
                using var added = await http.PutAsJsonAsync($"{engine.Url}/{name}/_doc/{Guid.NewGuid():N}", new
                {
                    block_id = hit.Id, file_name = "same.pdf", page_number = hit.Page, text = "the changing world",
                    _meta = new { block_data = "{\"pageNumber\":1}" },
                });
                added.EnsureSuccessStatusCode();
            }
            using (var refresh = await http.PostAsync($"{engine.Url}/{name}/_refresh", null)) refresh.EnsureSuccessStatusCode();
            var expectedLabels = new Dictionary<SearchBlockPageKey, string?>();
            foreach (var seed in new[] { first, other })
                for (var i = 0; i < seed.Blocks.Length; i++) expectedLabels[new(seed.Blocks[i], seed.Pages[i])] = seed.Labels[i];
            probe.Active = true;
            logs.Messages.Clear();
            string? token = null;
            var allIds = new List<string>();
            for (var page = 0; page < 10; page++)
            {
                var baseline = await index.ExactSearchAsync("changing", false, null, 7, token);
                var reads = probe.Reads.Count;
                var response = await SearchAsync(client, "query=changing&pageSize=7" + (token is null ? "" : "&pageToken=" + Uri.EscapeDataString(token)));
                AssertPage(response, baseline, expectedLabels);
                probe.Reads.Count.Should().Be(reads + 1, "one SQL join resolves every hit on the page");
                allIds.AddRange(baseline.Results.Select(hit => hit.SegmentId));
                token = baseline.NextToken;
                if (token is null) break;
            }
            token.Should().BeNull();
            allIds.Should().HaveCount(45);
            var filter = new SearchFilterModel { ParseId = first.Parse, PageNumber = 0, Subject = "test" };
            AssertPage(await SearchAsync(client, $"query=changing%20world&phrase=true&parseId={first.Parse}&pageNumber=0&subject=test&pageSize=100"),
                await index.ExactSearchAsync("changing world", true, filter, 100, null), expectedLabels);
            probe.Reads.Should().OnlyContain(read => read == IsolationLevel.RepeatableRead);
            probe.Commands.Should().OnlyContain(sql => sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                || sql == "SET TRANSACTION READ ONLY");
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
                (await db.DocumentParseBlocks.CountAsync(block => block.ParseId == first.Parse)).Should().Be(8);
                (await db.DocumentParsePages.CountAsync(page => page.ParseId == first.Parse)).Should().Be(5);
            }
            using (var mapping = JsonDocument.Parse(await http.GetStringAsync($"{engine.Url}/{name}/_mapping")))
                mapping.RootElement.GetProperty(name).GetProperty("mappings").GetProperty("_meta").GetProperty("mapping_version").GetInt32().Should().Be(3);
            logs.Messages.Should().NotContain(log => log.Contains("changing", StringComparison.OrdinalIgnoreCase));

            using (var removed = await http.DeleteAsync($"{engine.Url}/{name}")) removed.EnsureSuccessStatusCode();
            probe.Reads.Clear();
            logs.Messages.Clear();
            var empty = await SearchAsync(client, "query=private-query-canary");
            JsonNode.DeepEquals(empty, JsonNode.Parse("{\"results\":[],\"totalCount\":0,\"nextPageToken\":\"\"}")).Should().BeTrue();
            probe.Reads.Should().BeEmpty();
            logs.Messages.Should().Contain(log => log == "Warning:OpenSearch query failed; returning empty results");
            logs.Messages.Should().NotContain(log => log.Contains("canary", StringComparison.OrdinalIgnoreCase) || log.Contains(engine.Url, StringComparison.Ordinal));
        }
        finally { await engine.DisposeAsync(); }
    }

    [Fact]
    public async Task RelationalFailure_PreservesAllHitsAndEmitsOneSafeWarning()
    {
        var probe = new ReadProbe();
        var logs = new LogCapture();
        var hits = new List<SearchResultModel>();
        using var factory = Factory(probe, logs, services => ReplaceSearch(services, Search(hits)));
        using var client = Admin(factory);
        var seed = await SeedAsync(factory, "label");
        hits.AddRange(seed.Blocks.Take(2).Select(id => new SearchResultModel { SegmentId = id.ToString(), PageNumber = 0, AssociatedText = "synthetic", PageLabel = "untrusted" }));
        probe.Active = probe.Fail = true;
        logs.Messages.Clear();
        var result = await SearchAsync(client, "query=private-query-canary");
        result["results"]!.AsArray().Should().HaveCount(2).And.OnlyContain(hit => hit!["pageLabel"] == null);
        result["totalCount"]!.GetValue<int>().Should().Be(2);
        result["nextPageToken"]!.GetValue<string>().Should().Be("next");
        AssertPage(result, (hits, 2, "next"), new Dictionary<SearchBlockPageKey, string?>());
        logs.Messages.Where(message => message.StartsWith("Warning:", StringComparison.Ordinal)).Should()
            .Equal("Warning:Optional search page labels unavailable");
        logs.Messages.Should().NotContain(log => log.Contains("canary", StringComparison.OrdinalIgnoreCase) || log.Contains("untrusted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LocalLookupCancellation_PropagatesWithoutOptionalSuccess()
    {
        var probe = new ReadProbe();
        var logs = new LogCapture();
        var hits = new List<SearchResultModel>();
        using var factory = Factory(probe, logs, services => ReplaceSearch(services, Search(hits)));
        using var client = Admin(factory);
        var seed = await SeedAsync(factory, "label");
        hits.Add(new() { SegmentId = seed.Blocks[0].ToString(), PageNumber = 0 });
        probe.Active = probe.Pause = true;
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetAsync("/admin/documents/search?query=changing", cancellation.Token);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await probe.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        logs.Messages.Should().NotContain(message => message == "Warning:Optional search page labels unavailable");
    }

    [Fact]
    public async Task CancellationAfterSearch_DoesNotStartLocalLookup()
    {
        var probe = new ReadProbe { Active = true };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var search = new Mock<ISearchDomainService>();
        search.Setup(service => service.ExactSearchAsync(It.IsAny<string>(), false, null, 20, null)).Returns(async () =>
        {
            entered.TrySetResult(); await resume.Task; completed.TrySetResult();
            return (new List<SearchResultModel> { new() { SegmentId = Guid.NewGuid().ToString() } }, 1, (string?)null);
        });
        using var factory = Factory(probe, new(), services => ReplaceSearch(services, search.Object));
        using var client = Admin(factory);
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetAsync("/admin/documents/search?query=changing", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel(); resume.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        probe.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentParseReplacement_DoesNotCrossAssociateLabelsOrBlockWriter()
    {
        var probe = new ReadProbe();
        var hits = new List<SearchResultModel>();
        using var factory = Factory(probe, new(), services => ReplaceSearch(services, Search(hits)));
        using var client = Admin(factory);
        var old = await SeedAsync(factory, "old");
        var foreign = await SeedAsync(factory, "foreign");
        hits.AddRange(new[] { old, foreign }.Select(seed => new SearchResultModel { SegmentId = seed.Blocks[0].ToString(), PageNumber = 0 }));
        probe.Active = probe.PauseAfterRead = true;
        var pending = SearchAsync(client, "query=changing");
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
            await db.DocumentParses.Where(parse => parse.Id == old.Parse).ExecuteDeleteAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await SeedAsync(factory, "replacement");
        }
        finally { probe.Resume.TrySetResult(); }
        (await pending)["results"]!.AsArray().Select(hit => hit!["pageLabel"]?.GetValue<string>()).Should().Equal("old", "foreign");
        (await SearchAsync(client, "query=changing"))["results"]!.AsArray().Select(hit => hit!["pageLabel"]?.GetValue<string>()).Should().Equal(null, "foreign");
    }

    [Fact]
    public async Task ExistingAuthorizationValidationAndNoIdentityPaths_AvoidLookup()
    {
        var probe = new ReadProbe();
        var hits = new List<SearchResultModel> { new() { SegmentId = "invalid", PageLabel = "untrusted" }, new() { SegmentId = "", PageNumber = 0 } };
        using var factory = Factory(probe, new(), services => ReplaceSearch(services, Search(hits)));
        using var client = factory.CreateClient();
        (await client.GetAsync("/admin/documents/search?query=x")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("user"));
        (await client.GetAsync("/admin/documents/search?query=x")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin"));
        foreach (var query in new[] { "", "%20%20", new string('x', 201) })
            (await client.GetAsync("/admin/documents/search?query=" + query)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        probe.Active = true;
        (await SearchAsync(client, "query=x"))["results"]!.AsArray().Should().OnlyContain(hit => hit!["pageLabel"] == null);
        probe.Reads.Should().BeEmpty();
    }

    private WebApplicationFactory<Program> Factory(ReadProbe probe, LogCapture logs, Action<IServiceCollection>? configure = null,
        IReadOnlyDictionary<string, string?>? settings = null) => CreateFactory(configureTestServices: services =>
    {
        services.AddDbContext<DocthecaDbContext>(options => options.AddInterceptors(probe));
        services.RemoveAll<ILoggerFactory>();
        services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug)
            .AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None).AddProvider(logs)));
        configure?.Invoke(services);
    }, settings: settings);

    private static HttpClient Admin(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin"));
        return client;
    }
    private static ISearchDomainService Search(List<SearchResultModel> hits)
    {
        var search = new Mock<ISearchDomainService>();
        search.Setup(service => service.ExactSearchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<SearchFilterModel?>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ReturnsAsync(() => (hits.ToList(), hits.Count, "next"));
        return search.Object;
    }
    private static void ReplaceSearch(IServiceCollection services, ISearchDomainService search)
    {
        services.RemoveAll<ISearchDomainService>(); services.AddSingleton(search);
    }
    private static async Task<JsonNode> SearchAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync("/admin/documents/search?" + query);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }
    private static void AssertPage(JsonNode response, (List<SearchResultModel> Results, int TotalCount, string? NextToken) baseline,
        IReadOnlyDictionary<SearchBlockPageKey, string?> labels)
    {
        response["totalCount"]!.GetValue<int>().Should().Be(baseline.TotalCount);
        response["nextPageToken"]!.GetValue<string>().Should().Be(baseline.NextToken ?? "");
        var hits = response["results"]!.AsArray();
        hits.Should().HaveCount(baseline.Results.Count);
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = baseline.Results[i];
            var label = Guid.TryParse(hit.SegmentId, out var id) && labels.TryGetValue(new(id, hit.PageNumber), out var value) ? value : null;
            var expected = JsonSerializer.SerializeToNode(new
            {
                documentName = hit.DocumentName, pageNumber = hit.PageNumber, pageLabel = label, associatedText = hit.AssociatedText,
                score = hit.Score, matchType = hit.MatchType, segmentId = hit.SegmentId, startOffset = hit.StartOffset, endOffset = hit.EndOffset,
                createdAt = hit.CreatedAt?.ToString("o"), blockData = hit.BlockData, bbox = hit.Bbox, mineruScore = hit.MineruScore,
                subType = hit.SubType, textLevel = hit.TextLevel, textFormat = hit.TextFormat, caption = hit.Caption, structurePath = hit.StructurePath,
            });
            JsonNode.DeepEquals(hits[i], expected).Should().BeTrue("labels are the only addition to exact engine results");
        }
    }
    private sealed record Seed(Guid File, Guid Parse, Guid[] Blocks, int[] Pages, string?[] Labels);
    private static async Task<Seed> SeedAsync(WebApplicationFactory<Program> factory, string label, bool legacy = false, bool pages = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var file = new DocumentFileEntity { FileName = "same.pdf" };
        var parse = new DocumentParseEntity { DocumentFileId = file.Id, Status = DocumentParseStatus.Parsed, StructaDocParseRunId = legacy ? null : Guid.NewGuid() };
        int?[] source = [1, 2, 1, null, 3, 4, 5, 6];
        var blocks = source.Select((page, i) => new DocumentParseBlockEntity
        {
            ParseId = parse.Id, Sequence = i, SortIndex = i, PageId = (page ?? 1) - 1, BlockType = "text",
            TextContent = "the changing world block " + i,
            BlockData = new JsonObject { ["pageNumber"] = page }.ToJsonString(),
        }).ToArray();
        db.AddRange(file, parse); db.DocumentParseBlocks.AddRange(blocks); await db.SaveChangesAsync();
        if (pages) await new DocumentParsePageRepository(db).ReplaceAsync(parse.Id,
            [new(parse.Id, 1, label, null), new(parse.Id, 2, label, null), new(parse.Id, 3, "", null), new(parse.Id, 4, null, null), new(parse.Id, 6, "last", null)]);
        return new(file.Id, parse.Id, blocks.Select(block => block.Id).ToArray(), blocks.Select(block => block.PageId).ToArray(),
            [label, label, label, null, "", null, null, "last"]);
    }
    private sealed class ReadProbe : DbCommandInterceptor
    {
        public bool Active, Fail, Pause, PauseAfterRead;
        private int _paused;
        public ConcurrentQueue<IsolationLevel> Reads { get; } = new();
        public ConcurrentQueue<string> Commands { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static bool LabelQuery(DbCommand command) => command.CommandText.Contains("document_parse_blocks", StringComparison.Ordinal)
            && command.CommandText.Contains("document_parses", StringComparison.Ordinal) && command.CommandText.Contains("document_parse_pages", StringComparison.Ordinal);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Active || !LabelQuery(command)) return result;
            Reads.Enqueue(command.Transaction!.IsolationLevel); Commands.Enqueue(command.CommandText);
            if (Fail) throw new InvalidOperationException("database-provider-private-canary");
            if (Pause)
            {
                cancellationToken.CanBeCanceled.Should().BeTrue(); Entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            return result;
        }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Active && PauseAfterRead && LabelQuery(command) && Interlocked.Increment(ref _paused) == 1)
            { Entered.TrySetResult(); await Resume.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
            return result;
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Active && command.CommandText == "SET TRANSACTION READ ONLY") Commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
    private sealed class LogCapture : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(Messages);
        public void Dispose() { }
        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(logLevel + ":" + formatter(state, exception) + exception?.ToString());
        }
    }
}
