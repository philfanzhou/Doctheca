using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
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
public sealed class InternalCatalogEndpointTests(PostgreSqlFixture database) : ServiceMantleIntegrationTestBase(database)
{
    private const string Documents = "/internal/v1/documents";
    private const string Definitions = "/internal/v1/structure-definitions";

    private WebApplicationFactory<Program> Factory(string? enabled = "true", string? contentRoot = null,
        Action<IServiceCollection>? services = null) => CreateFactory(contentRoot, services,
        new Dictionary<string, string?> { ["InternalApi:Enabled"] = enabled });

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("configuration-value-canary")]
    [InlineData("true")]
    public async Task Boundary_ReservesEveryInternalPathAndMethodBeforeStaticFiles(string? enabled)
    {
        var root = CreateTempContentRoot(true);
        Directory.CreateDirectory(Path.Combine(root, "wwwroot", "internal"));
        await File.WriteAllTextAsync(Path.Combine(root, "wwwroot", "internal", "probe.txt"), "internal-static-canary");
        try
        {
            using var factory = Factory(enabled, root);
            using var client = factory.CreateClient();
            foreach (var method in new[] { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "TRACE" })
            foreach (var path in new[] { "/internal", "/INTERNAL/", "/internal/probe.txt", "/internal/v1/missing/nested.json",
                "/internal/v1/documents/00000000-0000-0000-0000-000000000001/structure", Documents, Definitions })
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path + "?enabled=true");
                request.Headers.TryAddWithoutValidation("Cookie", "InternalApi.Enabled=true");
                // Known GETs reject the unknown query; no request input can enable routes.
                using var response = await client.SendAsync(request);
                var knownEnabledGet = enabled == "true" && method == "GET" && path is Documents or Definitions;
                Assert.Equal(knownEnabledGet ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain("canary", body);
                Assert.DoesNotContain("html", body, StringComparison.OrdinalIgnoreCase);
                if (method != "HEAD" && !knownEnabledGet) Assert.Contains("DOCTHECA_INTERNAL_NOT_FOUND", body);
            }
            foreach (var path in new[] { Documents, Definitions })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(enabled == "true" ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
            }
            Assert.Contains("doctheca-stub-index-marker", await client.GetStringAsync("/internalized"));
            Assert.Equal("doctheca-static-probe", await client.GetStringAsync("/probe.txt"));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/document-files")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("user"));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/document-files")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin"));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/document-files")).StatusCode);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Catalogs_AreBoundedKeysetsWithExactFiltersAndOnlyAllowListedFields()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory);
        try
        {
            var query = $"{Documents}?structureDefinitionId={seed.Definitions[0]}&subject=%20Math%20&grade=%20G9%20&pageSize=1";
            var documents = await TraverseAsync(client, query);
            Assert.Equal(seed.Matching.Order(), documents.Select(item => item.GetProperty("id").GetGuid()));
            foreach (var item in documents)
            {
                AssertKeys(item, "id", "title", "subject", "grade", "structureDefinitionId", "structureDefinitionName");
                Assert.Equal("duplicate-title", item.GetProperty("title").GetString());
                Assert.Equal("duplicate-definition", item.GetProperty("structureDefinitionName").GetString());
                Assert.DoesNotContain("canary", item.ToString());
            }
            var all = await TraverseAsync(client, $"{Documents}?pageSize=2");
            Assert.Equal(all.Select(item => item.GetProperty("id").GetGuid()).Order(), all.Select(item => item.GetProperty("id").GetGuid()));
            Assert.Equal(all.Count, all.Select(item => item.GetProperty("id").GetGuid()).Distinct().Count());
            Assert.All(seed.Marked, id => Assert.Contains(all, item => item.GetProperty("id").GetGuid() == id));
            Assert.All(seed.Excluded, id => Assert.DoesNotContain(all, item => item.GetProperty("id").GetGuid() == id));
            var noMatch = await TraverseAsync(client, $"{Documents}?structureDefinitionId={seed.Definitions[0]}&subject=math");
            Assert.Empty(noMatch);
            var emptyFilters = await TraverseAsync(client, $"{Documents}?structureDefinitionId={seed.Definitions[0]}&subject=%20&grade=");
            Assert.Equal(5, emptyFilters.Count);

            var definitions = await TraverseAsync(client, Definitions + "?pageSize=1");
            Assert.Equal(definitions.Count, definitions.Select(item => item.GetProperty("id").GetGuid()).Distinct().Count());
            Assert.Equal(definitions.Select(item => item.GetProperty("id").GetGuid()).Order(), definitions.Select(item => item.GetProperty("id").GetGuid()));
            foreach (var id in seed.Definitions)
            {
                var item = Assert.Single(definitions, item => item.GetProperty("id").GetGuid() == id);
                AssertKeys(item, "id", "name", "levels");
                var levels = item.GetProperty("levels").EnumerateArray().ToArray();
                Assert.Equal(new[] { 0, 1 }, levels.Select(level => level.GetProperty("sortOrder").GetInt32()));
                foreach (var level in levels) AssertKeys(level, "id", "name", "parentLevelId", "sortOrder");
                Assert.Equal(levels[0].GetProperty("id").GetGuid(), levels[1].GetProperty("parentLevelId").GetGuid());
            }
            // Every new read remains untracked and never issues a write command.
            var writes = new RejectWrites();
            using var readOnlyFactory = Factory(services: services => services.AddDbContext<DocthecaDbContext>(options => options.AddInterceptors(writes)));
            using var readOnlyClient = readOnlyFactory.CreateClient();
            writes.Active = true;
            await Task.WhenAll(Enumerable.Range(0, 4).Select(async i =>
            {
                using var response = await readOnlyClient.GetAsync(i % 2 == 0 ? Documents : Definitions);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }));
            Assert.True(writes.Reads >= 6);
        }
        finally { await CleanupAsync(factory, seed); }
    }

    [Fact]
    public async Task InvalidInputsAndCursors_HaveFixedSafeErrorsAndUnknownDefinitionIs404()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory);
        try
        {
            var prefix = $"{Documents}?structureDefinitionId={seed.Definitions[0]}&pageSize=1";
            using var first = JsonDocument.Parse(await client.GetStringAsync(prefix));
            var token = first.RootElement.GetProperty("data").GetProperty("nextPageToken").GetString()!;
            Assert.NotNull(token);
            var errors = new List<string>
            {
                "pageSize=0", "pageSize=101", "pageSize=-1", "pageSize=1.5", "pageSize=abc-canary", "pageSize=1&pageSize=2",
                "structureDefinitionId=bad-canary", "structureDefinitionId=a&structureDefinitionId=b", "subject=" + new string('x', 101),
                "grade=" + new string('x', 101), "subject=a&subject=b", "unknown=value-canary", "pageToken=", "pageToken=***",
                "pageToken=" + new string('A', 2049), "pageToken=" + token, "pageToken=" + token + "="
            };
            foreach (var raw in new[] { "null", "[]", "{}", "{\"v\":2,\"after\":\"bad\",\"filter\":\"x\"}",
                "{\"v\":1,\"v\":1,\"after\":\"bad\",\"filter\":\"x\"}", "{\"v\":\"1\",\"after\":\"bad\",\"filter\":\"x\"}" })
                errors.Add("pageToken=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
            string? expected = null;
            foreach (var query in errors)
            {
                using var response = await client.GetAsync(Documents + "?" + query);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                expected ??= body;
                Assert.Equal(expected, body);
                Assert.DoesNotContain("canary", body);
            }
            using var mismatch = await client.GetAsync(prefix + "&subject=changed&pageToken=" + token);
            Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
            using var crossRoute = await client.GetAsync(Definitions + "?pageToken=" + token);
            Assert.Equal(HttpStatusCode.BadRequest, crossRoute.StatusCode);
            using var unknown = await client.GetAsync($"{Documents}?structureDefinitionId={Guid.NewGuid()}");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Contains("DOCTHECA_INTERNAL_DEFINITION_NOT_FOUND", await unknown.Content.ReadAsStringAsync());
        }
        finally { await CleanupAsync(factory, seed); }
    }

    [Fact]
    public async Task RelatedQueries_UseOneCommittedSnapshotDuringConcurrentChanges()
    {
        using var seedFactory = Factory();
        var seed = await SeedAsync(seedFactory);
        var pause = new SnapshotPause();
        using var factory = Factory(services: services => services.AddDbContext<DocthecaDbContext>(options => options.AddInterceptors(pause)));
        using var client = factory.CreateClient();
        try
        {
            pause.Active = true;
            var responseTask = client.GetAsync($"{Documents}?structureDefinitionId={seed.Definitions[0]}");
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using (var scope = seedFactory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
                await db.StructureDefinitions.Where(item => item.Id == seed.Definitions[0])
                    .ExecuteUpdateAsync(item => item.SetProperty(definition => definition.Name, "concurrent-name"));
                await db.DocumentFiles.Where(item => seed.Files.Contains(item.Id))
                    .ExecuteUpdateAsync(item => item.SetProperty(file => file.FileName, "concurrent-title"));
            }
            pause.Resume.SetResult();
            using var response = await responseTask;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.All(body.RootElement.GetProperty("data").GetProperty("items").EnumerateArray(), item =>
            {
                Assert.Equal("duplicate-definition", item.GetProperty("structureDefinitionName").GetString());
                Assert.Equal("duplicate-title", item.GetProperty("title").GetString());
            });
            Assert.Equal(System.Data.IsolationLevel.RepeatableRead, pause.Isolation);
        }
        finally { pause.Resume.TrySetResult(); await CleanupAsync(seedFactory, seed); }
    }

    [Fact]
    public async Task RelationalFailure_DoesNotLogProviderDetails()
    {
        var failure = new FailRead();
        var capture = new RequestScopeCapture();
        using var factory = Factory(services: services =>
        {
            services.AddDbContext<DocthecaDbContext>(options => options.AddInterceptors(failure));
            services.RemoveAll<ILoggerFactory>();
            services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(logging => logging.AddProvider(capture)));
        });
        using var client = factory.CreateClient();
        failure.Active = true;
        using var response = await client.GetAsync(Documents);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("provider-detail-canary", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(capture.Logs, item => item.Message.Contains("provider-detail-canary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failure_IsSafeAndCancellationFlowsToTheReadWithoutSuccessfulResponse()
    {
        var repository = new Mock<IInternalCatalogRepository>();
        repository.Setup(item => item.ReadDocumentsAsync(It.IsAny<InternalCatalogQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database-provider-canary"));
        using (var factory = Factory(services: services => { services.RemoveAll<IInternalCatalogRepository>(); services.AddSingleton(repository.Object); }))
        using (var client = factory.CreateClient())
        {
            using var response = await client.GetAsync(Documents);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("http.internal_server_error", body);
            Assert.DoesNotContain("canary", body);
            Assert.DoesNotContain("InvalidOperationException", body);
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.Setup(item => item.ReadDefinitionsAsync(It.IsAny<InternalCatalogQuery>(), It.IsAny<CancellationToken>()))
            .Returns(async (InternalCatalogQuery _, CancellationToken token) =>
            {
                Assert.True(token.CanBeCanceled);
                entered.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.SetResult(); throw; }
                return Array.Empty<InternalDefinition>();
            });
        using var cancellationFactory = Factory(services: services => { services.RemoveAll<IInternalCatalogRepository>(); services.AddSingleton(repository.Object); });
        using var cancellationClient = cancellationFactory.CreateClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = cancellationClient.GetAsync(Definitions, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The real relational implementation also refuses a pre-cancelled request.
        using var scope = cancellationFactory.Services.CreateScope();
        var real = new Doctheca.Database.Repositories.InternalCatalogRepository(scope.ServiceProvider.GetRequiredService<DbContextOptions<DocthecaDbContext>>());
        var query = new InternalCatalogQuery(20, null, null, null, null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => real.ReadDefinitionsAsync(query, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => real.ReadDocumentsAsync(query, cancellation.Token));
    }

    private static async Task<List<JsonElement>> TraverseAsync(HttpClient client, string url)
    {
        var result = new List<JsonElement>();
        string? token = null;
        for (var guard = 0; guard < 100; guard++)
        {
            using var response = await client.GetAsync(url + (token is null ? "" : "&pageToken=" + token));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(document.RootElement.GetProperty("success").GetBoolean());
            var data = document.RootElement.GetProperty("data");
            AssertKeys(data, "items", "nextPageToken");
            result.AddRange(data.GetProperty("items").EnumerateArray().Select(item => item.Clone()));
            var next = data.GetProperty("nextPageToken").GetString();
            if (next is null) return result;
            Assert.NotEqual(token, next);
            token = next;
        }
        throw new InvalidOperationException("Catalog traversal did not terminate.");
    }

    private static void AssertKeys(JsonElement element, params string[] keys) =>
        Assert.Equal(keys.Order(), element.EnumerateObject().Select(item => item.Name).Order());

    private sealed record Seed(Guid[] Definitions, Guid[] Files, Guid[] Marked, Guid[] Matching, Guid[] Excluded);

    private static async Task<Seed> SeedAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var definitions = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var levels = new List<Guid>();
        foreach (var definition in definitions)
        {
            var parent = Guid.NewGuid();
            var child = Guid.NewGuid();
            levels.Add(parent);
            db.StructureDefinitions.Add(new StructureDefinitionEntity { Id = definition, Name = "duplicate-definition" });
            db.StructureLevels.AddRange(new StructureLevelEntity { Id = parent, DefinitionId = definition, Name = "Unit", SortOrder = 0 },
                new StructureLevelEntity { Id = child, DefinitionId = definition, Name = "Section", ParentLevelId = parent, SortOrder = 1 });
        }
        var files = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        for (var i = 0; i < files.Length; i++)
        {
            db.DocumentFiles.Add(new DocumentFileEntity { Id = files[i], FileName = "duplicate-title", FilePath = "storage-path-canary",
                ContentType = "application/pdf", CreatedBy = Guid.NewGuid(), StructaDocDocumentId = Guid.NewGuid(),
                Subject = i == 3 ? " Math " : "Math", Grade = i == 4 ? "G8" : "G9", StructureDefinitionId = definitions[i == 5 ? 1 : 0] });
            var parse = Guid.NewGuid();
            db.DocumentParses.Add(new DocumentParseEntity { Id = parse, DocumentFileId = files[i],
                Status = i == 7 ? DocumentParseStatus.Failed : DocumentParseStatus.Parsed, ModelJson = "{\"provider\":\"raw-provider-canary\"}" });
            if (i != 6) db.DocumentStructureNodes.Add(new DocumentStructureNodeEntity { ParseId = parse, LevelId = levels[i == 5 ? 1 : 0],
                Name = "Unit", StartSequence = 0, EndSequence = 1 });
            if (i == 0) db.DocumentParses.Add(new DocumentParseEntity { DocumentFileId = files[i], Status = DocumentParseStatus.Pending });
        }
        await db.SaveChangesAsync();
        return new Seed(definitions, files, files.Take(6).ToArray(), files.Take(3).ToArray(), files.Skip(6).ToArray());
    }

    private static async Task CleanupAsync(WebApplicationFactory<Program> factory, Seed seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        await db.DocumentFiles.Where(item => seed.Files.Contains(item.Id)).ExecuteDeleteAsync();
        await db.StructureLevels.Where(item => seed.Definitions.Contains(item.DefinitionId)).ExecuteUpdateAsync(item => item.SetProperty(level => level.ParentLevelId, (Guid?)null));
        await db.StructureDefinitions.Where(item => seed.Definitions.Contains(item.Id)).ExecuteDeleteAsync();
    }

    private sealed class RejectWrites : DbCommandInterceptor
    {
        public bool Active;
        public int Reads;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Active)
            {
                Assert.StartsWith("SELECT", command.CommandText.TrimStart());
                Interlocked.Increment(ref Reads);
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Assert.False(Active, "Internal catalog reads must never issue writes.");
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class SnapshotPause : DbCommandInterceptor
    {
        public bool Active;
        public System.Data.IsolationLevel Isolation;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Active)
            {
                Active = false;
                Isolation = command.Transaction!.IsolationLevel;
                Entered.SetResult();
                await Resume.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private sealed class FailRead : DbCommandInterceptor
    {
        public bool Active;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Active && command.CommandText.Contains("WHERE EXISTS", StringComparison.Ordinal))
                throw new InvalidOperationException("provider-detail-canary");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
