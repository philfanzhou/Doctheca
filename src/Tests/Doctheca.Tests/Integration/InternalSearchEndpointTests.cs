using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;
using Doctheca.Service;
using Doctheca.Tests.OpenSearch;
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
public sealed class InternalSearchEndpointTests(PostgreSqlFixture database) : ServiceMantleIntegrationTestBase(database)
{
    private static readonly string InvalidBody = "{\"success\":false,\"errorCode\":\"DOCTHECA_INTERNAL_INVALID\",\"message\":\"Invalid internal API request.\"}";
    private static readonly string MissingBody = "{\"success\":false,\"errorCode\":\"DOCTHECA_INTERNAL_RESOURCE_NOT_FOUND\",\"message\":\"Internal resource not found.\"}";
    private static string Url(string query = "changing", Guid? node = null) => "/internal/v1/search?query=" + Uri.EscapeDataString(query) +
        (node is null ? "" : "&structureNodeId=" + node);
    private static string Token(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    private WebApplicationFactory<Program> Factory(Action<IServiceCollection>? services = null, string? enabled = "true") =>
        CreateFactory(configureTestServices: services, settings: new Dictionary<string, string?> { ["InternalApi:Enabled"] = enabled });
    private static Mock<ISearchDomainService> EmptySearch()
    {
        var search = new Mock<ISearchDomainService>();
        search.Setup(item => item.ExactSearchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<SearchFilterModel?>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ReturnsAsync((new List<SearchResultModel>(), 0, null));
        return search;
    }
    private static void ReplaceSearch(IServiceCollection services, ISearchDomainService search)
    {
        services.RemoveAll<ISearchDomainService>();
        services.AddSingleton(search);
    }

    [Fact]
    public async Task RealEngineAndPostgreSql_KeepSubtreesPhrasePagingAndSafeFailureSemantics()
    {
        var engine = new OpenSearchFixture();
        await engine.InitializeAsync();
        try
        {
            var indexName = "internal-search-engine-canary-" + Guid.NewGuid().ToString("N");
            var capture = new LogCapture();
            var probe = new QueryProbe();
            using var factory = CreateFactory(configureTestServices: services =>
            {
                services.RemoveAll<ISearchIndexService>();
                services.AddSingleton<ISearchIndexService, OpenSearchIndexService>();
                services.AddDbContext<DocthecaDbContext>(options => options.AddInterceptors(probe));
                CaptureLogs(services, capture);
            }, settings: new Dictionary<string, string?> { ["InternalApi:Enabled"] = "true", ["OpenSearch:Url"] = engine.Url, ["OpenSearch:IndexName"] = indexName });
            using var client = factory.CreateClient();
            var seed = await SeedAsync(factory);
            try
            {
                var index = factory.Services.GetRequiredService<ISearchIndexService>();
                Assert.True(await index.IndexParseBlocksAsync(seed.Parse, seed.File, "book.pdf", null, null, null));
                using var http = new HttpClient();
                using (var refresh = await http.PostAsync($"{engine.Url}/{indexName}/_refresh", null)) refresh.EnsureSuccessStatusCode();
                probe.Active = true;
                capture.Messages.Clear();
                async Task<JsonElement[]> Hits(Guid? node, bool phrase = false)
                {
                    using var response = await client.GetAsync(Url(phrase ? "changing world" : "changing", node) + "&pageSize=100&phrase=" + phrase);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.True(response.Headers.Contains(CorrelationHeaderName));
                    Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    return body.RootElement.GetProperty("data").GetProperty("results").EnumerateArray().Select(item => item.Clone()).ToArray();
                }
                var all = await Hits(null);
                Assert.Equal(12, all.Length);
                var unit = await Hits(seed.Unit);
                var section = await Hits(seed.Section);
                Assert.Equal(seed.Blocks.Take(6).Select(id => id.ToString()).Order(), unit.Select(item => item.GetProperty("segmentId").GetString()).Order());
                Assert.Equal(seed.Blocks.Take(3).Select(id => id.ToString()).Order(), section.Select(item => item.GetProperty("segmentId").GetString()).Order());
                Assert.Equal(4, (await Hits(seed.Unit, phrase: true)).Length);
                Assert.All(section, hit =>
                {
                    AssertKeys(hit, "documentName", "pageNumber", "associatedText", "score", "matchType", "segmentId", "startOffset", "endOffset", "structurePath");
                    Assert.Equal("Unit A > Section A", hit.GetProperty("structurePath").GetString());
                    Assert.Equal(0, hit.GetProperty("pageNumber").GetInt32());
                    Assert.Equal(0, hit.GetProperty("startOffset").GetInt32());
                    Assert.Equal(0, hit.GetProperty("endOffset").GetInt32());
                });
                var ids = new List<string>();
                string? token = null;
                for (var page = 0; page < 10; page++)
                {
                    using var response = await client.GetAsync(Url() + "&pageSize=2" + (token is null ? "" : "&pageToken=" + Uri.EscapeDataString(token)));
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    var data = body.RootElement.GetProperty("data");
                    AssertKeys(body.RootElement, "success", "data");
                    AssertKeys(data, "results", "totalCount", "nextPageToken");
                    Assert.Equal(12, data.GetProperty("totalCount").GetInt32());
                    ids.AddRange(data.GetProperty("results").EnumerateArray().Select(item => item.GetProperty("segmentId").GetString()!));
                    token = data.GetProperty("nextPageToken").GetString();
                    if (token is null) break;
                    Assert.InRange(token.Length, 1, 4096);
                    Assert.True(Service.InternalApi.InternalSearchInput.ValidToken(token));
                }
                Assert.Null(token);
                Assert.Equal(12, ids.Count);
                Assert.Equal(12, ids.Distinct().Count());
                Assert.Equal(all.Select(item => item.GetProperty("segmentId").GetString()).Order(), ids.Order());
                Assert.NotEmpty(probe.Commands);
                Assert.All(probe.Commands, sql => Assert.StartsWith("SELECT", sql.TrimStart(), StringComparison.OrdinalIgnoreCase));
                probe.Active = false;
                using (var scope = factory.Services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
                    Assert.Equal(12, await db.DocumentParseBlocks.CountAsync(item => item.ParseId == seed.Parse));
                    Assert.Equal(4, await db.DocumentStructureNodes.CountAsync(item => item.ParseId == seed.Parse));
                }
                using (var count = JsonDocument.Parse(await http.GetStringAsync($"{engine.Url}/{indexName}/_count"))) Assert.Equal(12, count.RootElement.GetProperty("count").GetInt32());
                Assert.DoesNotContain(capture.Messages, log => log.Contains("changing", StringComparison.OrdinalIgnoreCase) || log.Contains("raw-provider-canary", StringComparison.Ordinal));

                // A real engine error body contains the missing index name. Neither the
                // index exception nor the domain/HTTP warning may retain that body or query.
                using (var deleted = await http.DeleteAsync($"{engine.Url}/{indexName}")) deleted.EnsureSuccessStatusCode();
                capture.Messages.Clear();
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => index.ExactSearchAsync("private-query-canary", false, null, 20, null));
                Assert.Equal("OpenSearch query failed.", failure.Message);
                using var empty = await client.GetAsync(Url("private-query-canary"));
                Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
                Assert.Equal("{\"success\":true,\"data\":{\"results\":[],\"totalCount\":0,\"nextPageToken\":null}}", await empty.Content.ReadAsStringAsync());
                Assert.Contains(capture.Messages, log => log.Contains("Warning:OpenSearch query failed; returning empty results", StringComparison.Ordinal));
                Assert.DoesNotContain(capture.Messages, log => log.Contains("canary", StringComparison.OrdinalIgnoreCase) || log.Contains(engine.Url, StringComparison.Ordinal));
            }
            finally { probe.Active = false; await CleanupAsync(factory, seed); }
        }
        finally { await engine.DisposeAsync(); }
    }

    [Fact]
    public async Task InvalidInputs_AreFixed400AndNeverRestartOrCallSearch()
    {
        var search = EmptySearch();
        using var factory = Factory(services => ReplaceSearch(services, search.Object));
        using var client = factory.CreateClient();
        var bad = new List<string>
        {
            "", "query=", "query=%20%20", "query=" + new string('x', 201), "query=x&query=y", "Query=x", "query=x&unknown=raw-canary",
            "query=x&phrase=", "query=x&phrase=1", "query=x&phrase=%20true", "query=x&phrase=true&phrase=false",
            "query=x&structureNodeId=", "query=x&structureNodeId=bad-canary", "query=x&structureNodeId=" + Guid.NewGuid().ToString("N"),
            "query=x&structureNodeId=%20" + Guid.NewGuid(), "query=x&structureNodeId=" + Guid.NewGuid() + "%20",
            "query=x&pageSize=", "query=x&pageSize=0", "query=x&pageSize=101", "query=x&pageSize=-1", "query=x&pageSize=%2B1",
            "query=x&pageSize=1.0", "query=x&pageSize=%201", "query=x&pageSize=999999999999", "query=x&pageSize=1&pageSize=2",
            "query=x&pageToken=", "query=x&pageToken=raw-canary!", "query=x&pageToken=" + new string('A', 4100)
        };
        string[] badJson = ["null", "{}", "[]", "[1]", "[1,\"id\",2]", "[\"1\",\"id\"]", "[null,\"id\"]", "[1e9999,\"id\"]", "[1,1]", "[1,null]", "[1,\"\"]", "[1,\" \"]", "[1,\"\\n\"]", "[1,\"" + new string('x', 257) + "\"]", "[[[[]]],\"id\"]", "[1,\"id\"] trailing"];
        bad.AddRange(badJson.Select(value => "query=x&pageToken=" + Uri.EscapeDataString(Token(value))));
        bad.Add("query=x&pageToken=" + Uri.EscapeDataString(Token("[1,\"id\"]") + "\n"));
        foreach (var parameters in bad)
        {
            using var response = await client.GetAsync("/internal/v1/search?" + parameters);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(InvalidBody, await response.Content.ReadAsStringAsync());
        }
        search.Verify(item => item.ExactSearchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<SearchFilterModel?>(), It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task MalformedUnicodeTokens_AreFixed400BeforeSearchAndValidPairsRemainUsable()
    {
        var search = EmptySearch();
        var capture = new LogCapture();
        using var factory = Factory(services =>
        {
            ReplaceSearch(services, search.Object);
            CaptureLogs(services, capture);
        });
        using var client = factory.CreateClient();
        capture.Messages.Clear();
        string[] malformed =
        [
            """[1,"\uD800"]""", """[1,"\uDC00"]""", """[1,"\uD800x"]""",
            """[1,"\uDC00\uD800"]""", """[1,"\uD800\uD800"]""", """[1,"cursor-canary\uD800"]"""
        ];
        foreach (var json in malformed)
        {
            var token = Token(json);
            using var response = await client.GetAsync(Url("query-canary") + "&pageToken=" + Uri.EscapeDataString(token));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(InvalidBody, await response.Content.ReadAsStringAsync());
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.True(response.Headers.Contains(CorrelationHeaderName));
            (string Name, string Value)[] headers =
            [
                ("Cache-Control", "no-store"), ("Pragma", "no-cache"), ("X-Content-Type-Options", "nosniff"),
                ("X-Frame-Options", "DENY"), ("Referrer-Policy", "no-referrer"),
                ("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'")
            ];
            foreach (var header in headers) Assert.Equal(header.Value, Assert.Single(response.Headers.GetValues(header.Name)));
            Assert.DoesNotContain(capture.Messages, log => log.Contains(token, StringComparison.Ordinal) || log.Contains(json, StringComparison.Ordinal));
        }
        search.Verify(item => item.ExactSearchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<SearchFilterModel?>(), It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
        Assert.DoesNotContain(capture.Messages, log => log.Contains("canary", StringComparison.Ordinal));

        // Both the input and returned continuation retain valid supplementary characters.
        foreach (var json in new[] { """[1,"\uD800\uDC00"]""", """[1,"id\uDBFF\uDFFFtail"]""" })
        {
            var token = Token(json);
            search.Setup(item => item.ExactSearchAsync("changing", false, null, 20, token))
                .ReturnsAsync((new List<SearchResultModel>(), 0, token));
            using var response = await client.GetAsync(Url() + "&pageToken=" + Uri.EscapeDataString(token));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(token, body.RootElement.GetProperty("data").GetProperty("nextPageToken").GetString());
            search.Verify(item => item.ExactSearchAsync("changing", false, null, 20, token), Times.Once);
        }
    }

    [Fact]
    public async Task ValidInputs_ProjectOnlyAllowedFieldsAndKeepNoNodeSemantics()
    {
        var search = EmptySearch();
        var token = Token("[1.25,\"block-id\"]");
        search.Setup(item => item.ExactSearchAsync("hello", true, null, 100, token)).ReturnsAsync((new List<SearchResultModel>
        {
            new() { DocumentName = "book.pdf", AssociatedText = "<script>untrusted</script>", PageNumber = 0, Score = 1.25,
                MatchType = "exact_phrase", SegmentId = "block-id", StructurePath = "Unit", BlockData = "raw-provider-canary",
                Bbox = [1,2,3,4], MineruScore = 0.7, SubType = "admin-canary", TextLevel = 1, TextFormat = "admin-canary", Caption = "admin-canary", PageLabel = "admin-canary", CreatedAt = DateTimeOffset.UtcNow }
        }, 1, token));
        using var factory = Factory(services => ReplaceSearch(services, search.Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Url("  hello  ") + "&phrase=TRUE&pageSize=100&pageToken=" + Uri.EscapeDataString(token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("canary", text);
        using var body = JsonDocument.Parse(text);
        var hit = Assert.Single(body.RootElement.GetProperty("data").GetProperty("results").EnumerateArray());
        AssertKeys(hit, "documentName", "pageNumber", "associatedText", "score", "matchType", "segmentId", "startOffset", "endOffset", "structurePath");
        Assert.Equal("<script>untrusted</script>", hit.GetProperty("associatedText").GetString());
        Assert.Equal(token, body.RootElement.GetProperty("data").GetProperty("nextPageToken").GetString());
        search.Verify(item => item.ExactSearchAsync("hello", true, null, 100, token), Times.Once);
        using var defaults = await client.GetAsync(Url("  hello  "));
        Assert.Equal(HttpStatusCode.OK, defaults.StatusCode);
        search.Verify(item => item.ExactSearchAsync("hello", false, null, 20, null), Times.Once);
        using var minimum = await client.GetAsync(Url() + "&pageSize=1&phrase=false&pageToken=" + Uri.EscapeDataString(Token("[-1.5,\"" + new string('x', 256) + "\"]")));
        Assert.Equal(HttpStatusCode.OK, minimum.StatusCode);
        using var maximum = await client.GetAsync(Url(new string('x', 200)));
        Assert.Equal(HttpStatusCode.OK, maximum.StatusCode);
    }

    [Fact]
    public async Task MissingStaleAndInvalidDefinitionNodes_Are404BeforeSearch()
    {
        var search = EmptySearch();
        using var factory = Factory(services => ReplaceSearch(services, search.Object));
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory);
        try
        {
            async Task Missing(Guid node)
            {
                using var response = await client.GetAsync(Url(node: node));
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Equal(MissingBody, await response.Content.ReadAsStringAsync());
            }
            await Missing(Guid.NewGuid());
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
            var newer = Guid.NewGuid();
            var newestNode = Guid.NewGuid();
            db.DocumentParses.Add(new DocumentParseEntity { Id = newer, DocumentFileId = seed.File, Status = DocumentParseStatus.Parsed, ParsedAt = DateTimeOffset.UtcNow.AddDays(1) });
            db.DocumentStructureNodes.Add(new DocumentStructureNodeEntity { Id = newestNode, ParseId = newer, LevelId = seed.Level, Name = "new", StartSequence = 0, EndSequence = 5 });
            await db.SaveChangesAsync();
            await Missing(seed.Unit);
            await db.DocumentParses.Where(item => item.Id == newer).ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, DocumentParseStatus.Failed));
            await Missing(newestNode);
            await db.DocumentFiles.Where(item => item.Id == seed.File).ExecuteUpdateAsync(set => set.SetProperty(item => item.StructureDefinitionId, (Guid?)null));
            await Missing(seed.Unit);
            search.Verify(item => item.ExactSearchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<SearchFilterModel?>(), It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
        }
        finally { await CleanupAsync(factory, seed); }
    }

    [Fact]
    public async Task ConcurrentTreeReplacement_DoesNotLockWriterOrPromiseAnIndexSnapshot()
    {
        var search = EmptySearch();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SearchFilterModel? actual = null;
        search.Setup(item => item.ExactSearchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<SearchFilterModel?>(), It.IsAny<int>(), It.IsAny<string?>()))
            .Returns(async (string text, bool phrase, SearchFilterModel? filter, int size, string? token) =>
            { actual = filter; entered.TrySetResult(); await resume.Task; return (new List<SearchResultModel>(), 0, (string?)null); });
        using var factory = Factory(services => ReplaceSearch(services, search.Object));
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory);
        try
        {
            var reading = client.GetAsync(Url(node: seed.Unit));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
                await db.DocumentStructureNodes.Where(item => item.ParseId == seed.Parse).ExecuteDeleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.Equal(seed.Parse, actual!.ParseId);
            Assert.Equal(seed.Unit, actual.StructureNodeId);
            resume.TrySetResult();
            using var completed = await reading;
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
            using var next = await client.GetAsync(Url(node: seed.Unit));
            Assert.Equal(HttpStatusCode.NotFound, next.StatusCode);
            search.Verify(item => item.ExactSearchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<SearchFilterModel?>(), It.IsAny<int>(), It.IsAny<string?>()), Times.Once);
        }
        finally { resume.TrySetResult(); await CleanupAsync(factory, seed); }
    }

    [Fact]
    public async Task RelationalFailure_IsSafeAndDoesNotCallSearch()
    {
        var search = EmptySearch();
        var capture = new LogCapture();
        var probe = new QueryProbe { Fail = true };
        using var factory = Factory(services =>
        {
            ReplaceSearch(services, search.Object);
            CaptureLogs(services, capture);
            services.AddDbContext<DocthecaDbContext>(options => options.AddInterceptors(probe));
        });
        using var client = factory.CreateClient();
        capture.Messages.Clear();
        probe.Active = true;
        using var response = await client.GetAsync(Url("private-query-canary", Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("http.internal_server_error", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("canary", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(capture.Messages, log => log.Contains("canary", StringComparison.Ordinal));
        search.Verify(item => item.ExactSearchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<SearchFilterModel?>(), It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task CancellationAfterRemoteReadAndDuringNodeRead_NeverReturnsSuccess()
    {
        var search = EmptySearch();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        search.Setup(item => item.ExactSearchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<SearchFilterModel?>(), It.IsAny<int>(), It.IsAny<string?>()))
            .Returns(async () => { entered.TrySetResult(); await resume.Task; completed.TrySetResult(); return (new List<SearchResultModel>(), 0, (string?)null); });
        using var factory = Factory(services => ReplaceSearch(services, search.Object));
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = client.GetAsync(Url(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        resume.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var probe = new QueryProbe { Pause = true };
        using var nodeFactory = Factory(services => services.AddDbContext<DocthecaDbContext>(options => options.AddInterceptors(probe)));
        using var nodeClient = nodeFactory.CreateClient();
        probe.Active = true;
        using var nodeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var nodeRequest = nodeClient.GetAsync(Url(node: Guid.NewGuid()), nodeCancellation.Token);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        nodeCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nodeRequest);
        await probe.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DomainFailureDiagnostics_AreSafeAndKeepExistingDependencyTimeoutSemantics()
    {
        var index = new Mock<ISearchIndexService>();
        var capture = new LogCapture();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(capture));
        var domain = new SearchDomainService(index.Object, loggerFactory.CreateLogger<SearchDomainService>());
        index.Setup(item => item.ExactSearchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<SearchFilterModel?>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("raw-provider-canary query-canary secret-canary"));
        var result = await domain.ExactSearchAsync("query-canary", false, null, 20, "token-canary");
        Assert.Empty(result.Results);
        Assert.Equal(0, result.TotalCount);
        Assert.Null(result.NextToken);
        Assert.Contains(capture.Messages, log => log.Contains("Warning:OpenSearch query failed; returning empty results", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Messages, log => log.Contains("canary", StringComparison.Ordinal));
        index.Setup(item => item.ExactSearchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<SearchFilterModel?>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ThrowsAsync(new OperationCanceledException("cancel-canary"));
        // This interface has no caller token. An engine timeout retains the existing empty
        // failure outcome; the HTTP endpoint separately observes RequestAborted after await.
        var timeout = await domain.ExactSearchAsync("query-canary", false, null, 20, null);
        Assert.Empty(timeout.Results);
        Assert.Equal(0, timeout.TotalCount);
        Assert.Null(timeout.NextToken);
        // Administrative invalid-token fallback stays compatible, with no raw JSON error.
        var body = Service.OpenSearch.OpenSearchQueryBuilder.BuildSearchBody("query-canary", false, null, 20, Token("[raw-token-canary"), loggerFactory.CreateLogger("query"));
        Assert.False(body.ContainsKey("search_after"));
        Assert.DoesNotContain(capture.Messages, log => log.Contains("canary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DefaultOffAndUnsupportedMethods_Are404AndAdminRemainsProtected()
    {
        foreach (var setting in new string?[] { null, "false", "invalid" })
        {
            using var disabled = Factory(enabled: setting);
            using var disabledClient = disabled.CreateClient();
            using var response = await disabledClient.GetAsync(Url());
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("DOCTHECA_INTERNAL_NOT_FOUND", await response.Content.ReadAsStringAsync());
        }
        using var factory = Factory();
        using var client = factory.CreateClient();
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Head })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(method, Url()));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var anonymous = await client.GetAsync("/admin/documents/search?query=x");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("user"));
        using var user = await client.GetAsync("/admin/documents/search?query=x");
        Assert.Equal(HttpStatusCode.Forbidden, user.StatusCode);
    }

    private sealed record Seed(Guid File, Guid Parse, Guid Definition, Guid Level, Guid Unit, Guid Section, Guid[] Blocks);
    private static async Task<Seed> SeedAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var file = Guid.NewGuid(); var parse = Guid.NewGuid(); var definition = Guid.NewGuid(); var level = Guid.NewGuid(); var childLevel = Guid.NewGuid();
        var unit = Guid.NewGuid(); var section = Guid.NewGuid();
        db.StructureDefinitions.Add(new StructureDefinitionEntity { Id = definition, Name = "Textbook" });
        db.StructureLevels.AddRange(new StructureLevelEntity { Id = level, DefinitionId = definition, Name = "Unit" },
            new StructureLevelEntity { Id = childLevel, DefinitionId = definition, Name = "Section", ParentLevelId = level });
        db.DocumentFiles.Add(new DocumentFileEntity { Id = file, FileName = "book.pdf", StructureDefinitionId = definition });
        db.DocumentParses.Add(new DocumentParseEntity { Id = parse, DocumentFileId = file, Status = DocumentParseStatus.Parsed, ParsedAt = DateTimeOffset.UtcNow });
        db.DocumentStructureNodes.AddRange(new DocumentStructureNodeEntity { Id = unit, ParseId = parse, LevelId = level, Name = "Unit A", StartSequence = 0, EndSequence = 5 },
            new DocumentStructureNodeEntity { Id = section, ParseId = parse, LevelId = childLevel, ParentId = unit, Name = "Section A", StartSequence = 0, EndSequence = 2 },
            new DocumentStructureNodeEntity { ParseId = parse, LevelId = childLevel, ParentId = unit, Name = "Section B", StartSequence = 3, EndSequence = 5 },
            new DocumentStructureNodeEntity { ParseId = parse, LevelId = level, Name = "Unit B", StartSequence = 6, EndSequence = 8 });
        var blocks = Enumerable.Range(0, 12).Select(i => new DocumentParseBlockEntity
        {
            Id = Guid.NewGuid(), ParseId = parse, PageId = i / 3, Sequence = i, SortIndex = i % 3, BlockType = "text",
            TextContent = (i == 1 ? "the changed world block " : i == 2 ? "the changing wider world block " : "the changing world block ") + i,
            BlockData = "{\"provider\":\"raw-provider-canary\"}"
        }).ToArray();
        db.DocumentParseBlocks.AddRange(blocks);
        await db.SaveChangesAsync();
        return new(file, parse, definition, level, unit, section, blocks.Select(item => item.Id).ToArray());
    }
    private static async Task CleanupAsync(WebApplicationFactory<Program> factory, Seed seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        await db.DocumentFiles.Where(item => item.Id == seed.File).ExecuteDeleteAsync();
        await db.StructureLevels.Where(item => item.DefinitionId == seed.Definition).ExecuteUpdateAsync(set => set.SetProperty(item => item.ParentLevelId, (Guid?)null));
        await db.StructureDefinitions.Where(item => item.Id == seed.Definition).ExecuteDeleteAsync();
    }
    private static void AssertKeys(JsonElement value, params string[] keys) => Assert.Equal(keys.Order(), value.EnumerateObject().Select(item => item.Name).Order());
    private static void CaptureLogs(IServiceCollection services, LogCapture capture)
    {
        services.RemoveAll<ILoggerFactory>();
        services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug)
            .AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None).AddProvider(capture)));
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
    private sealed class QueryProbe : DbCommandInterceptor
    {
        public bool Active; public bool Fail; public bool Pause;
        public ConcurrentQueue<string> Commands { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Active) return result;
            Commands.Enqueue(command.CommandText);
            if (Fail) throw new InvalidOperationException("database-provider-canary");
            if (Pause)
            {
                Entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            return result;
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Active) throw new InvalidOperationException("Unexpected write during internal search.");
            return ValueTask.FromResult(result);
        }
    }
}
