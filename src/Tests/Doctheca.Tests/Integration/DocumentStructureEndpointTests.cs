using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Domain.Repositories;
using Xunit;

namespace Doctheca.Tests.Integration;

/// <summary>
/// Issue #89 end to end through the real host and PostgreSQL: runtime structure definitions,
/// choosing one for a document, paging the blocks of a parse in sequence order, saving and
/// reloading a hand-marked tree, node block ranges, every rejected save, 409/404 rules, deletion
/// cascade, concurrent saves, and the admin authorization boundary of the new routes.
/// </summary>
[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed class DocumentStructureEndpointTests(PostgreSqlFixture database) : ServiceMantleIntegrationTestBase(database)
{
    private readonly RecordingSearchIndex _searchIndex = new();

    private WebApplicationFactory<Program> Factory() => CreateFactory(configureTestServices: services =>
    {
        services.RemoveAll<ISearchIndexService>();
        services.AddSingleton<ISearchIndexService>(_searchIndex);
    });

    private static HttpClient Admin(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin"));
        return client;
    }

    [Theory]
    [InlineData("GET", "/admin/structure-definitions")]
    [InlineData("POST", "/admin/structure-definitions")]
    [InlineData("GET", "/admin/document-parses/00000000-0000-0000-0000-000000000001/blocks")]
    [InlineData("GET", "/admin/document-parses/00000000-0000-0000-0000-000000000001/structure")]
    [InlineData("PUT", "/admin/document-parses/00000000-0000-0000-0000-000000000001/structure")]
    [InlineData("GET", "/admin/document-parses/00000000-0000-0000-0000-000000000001/structure/nodes/00000000-0000-0000-0000-000000000002/blocks")]
    [InlineData("POST", "/admin/document-parses/00000000-0000-0000-0000-000000000001/search-index")]
    [InlineData("POST", "/admin/search-index/reindex")]
    public async Task NewRoutes_RequireAnAdminSession(string method, string path)
    {
        using var factory = Factory();
        using var anonymous = factory.CreateClient();
        using var user = factory.CreateClient();
        user.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("user"));

        (await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), path))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await user.SendAsync(new HttpRequestMessage(new HttpMethod(method), path))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MissingParseNodeOrDefinition_Returns404()
    {
        using var factory = Factory();
        using var client = Admin(factory);
        var missing = Guid.NewGuid();
        var (_, parseId) = await SeedDocumentAsync(factory, 5);

        (await client.GetAsync($"/admin/document-parses/{missing}/blocks")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/admin/document-parses/{missing}/structure")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync($"/admin/document-parses/{missing}/structure", new { nodes = Array.Empty<object>() })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/admin/document-parses/{parseId}/structure/nodes/{missing}/blocks")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsync($"/admin/document-parses/{missing}/search-index", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/admin/structure-definitions/{missing}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync($"/admin/structure-definitions/{missing}", new { name = "x", levels = new[] { new { name = "L" } } })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.DeleteAsync($"/admin/structure-definitions/{missing}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Textbook_IsConfiguredMarkedSavedReloadedAndProtected()
    {
        using var factory = Factory();
        using var client = Admin(factory);

        // 1. A definition with two levels, created at runtime.
        var unitId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var created = await client.PostAsJsonAsync("/admin/structure-definitions", new
        {
            name = "English textbook",
            levels = new object[]
            {
                new { id = unitId, name = "Unit", parentLevelId = (Guid?)null },
                new { id = sectionId, name = "Section", parentLevelId = unitId },
            },
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var definition = (await ReadData(created))!;
        var definitionId = definition["id"]!.GetValue<string>();
        definition["levels"]!.AsArray().Select(l => l!["name"]!.GetValue<string>()).Should().Equal("Unit", "Section");

        // 2. The document chooses it through the existing metadata update.
        var (fileId, parseId) = await SeedDocumentAsync(factory, 400);
        var assigned = await client.PutAsJsonAsync($"/admin/document-files/{fileId}/metadata", new { structureDefinitionId = definitionId });
        assigned.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadData(await client.GetAsync($"/admin/document-files/{fileId}")))!["structureDefinitionId"]!.GetValue<string>().Should().Be(definitionId);

        // 3. Blocks page through the whole parse in sequence order, no gaps or duplicates.
        var sequences = new List<int>();
        int? after = null;
        for (var guard = 0; guard < 50; guard++)
        {
            var page = (await ReadData(await client.GetAsync(
                $"/admin/document-parses/{parseId}/blocks?limit=37" + (after is null ? "" : $"&afterSequence={after}"))))!;
            var items = page["items"]!.AsArray();
            sequences.AddRange(items.Select(i => i!["sequence"]!.GetValue<int>()));
            if (!page["hasMore"]!.GetValue<bool>()) break;
            after = page["nextAfterSequence"]!.GetValue<int>();
        }
        sequences.Should().Equal(Enumerable.Range(0, 400));
        var first = (await ReadData(await client.GetAsync($"/admin/document-parses/{parseId}/blocks?limit=1")))!["items"]![0]!;
        first["pageNumber"]!.GetValue<int>().Should().Be(1);
        first["content"]!.GetValue<string>().Should().Be("block 0");
        first["blockType"]!.GetValue<string>().Should().Be("text");

        // 4. Eight units with their sections; saved, reloaded, identical.
        var tree = TextbookTree(unitId, sectionId);
        var saved = await client.PutAsJsonAsync($"/admin/document-parses/{parseId}/structure", new { nodes = tree });
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        _searchIndex.IndexedParses.Should().Contain(Guid.Parse(parseId));
        var reloaded = (await ReadData(await client.GetAsync($"/admin/document-parses/{parseId}/structure")))!;
        Canonical(reloaded["nodes"]!.AsArray()).Should().Equal(Canonical(JsonSerializer.SerializeToNode(tree)!.AsArray()));
        reloaded["nodes"]![2]!["levelName"]!.GetValue<string>().Should().Be("Unit");
        reloaded["nodes"]![2]!["children"]![1]!["levelName"]!.GetValue<string>().Should().Be("Section");
        reloaded["definition"]!["name"]!.GetValue<string>().Should().Be("English textbook");

        // 5. A node's blocks are exactly its range (unit 3, section B = sequences 120..139).
        var sectionB = reloaded["nodes"]![2]!["children"]![1]!["id"]!.GetValue<string>();
        var nodeBlocks = new List<int>();
        after = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var page = (await ReadData(await client.GetAsync(
                $"/admin/document-parses/{parseId}/structure/nodes/{sectionB}/blocks?limit=7" + (after is null ? "" : $"&afterSequence={after}"))))!;
            nodeBlocks.AddRange(page["items"]!.AsArray().Select(i => i!["sequence"]!.GetValue<int>()));
            if (!page["hasMore"]!.GetValue<bool>()) break;
            after = page["nextAfterSequence"]!.GetValue<int>();
        }
        nodeBlocks.Should().Equal(Enumerable.Range(120, 20));

        // 6. Every invalid save is a 400 with a field reason and leaves the stored tree unchanged.
        var otherLevel = Guid.NewGuid();
        var otherDefinition = await client.PostAsJsonAsync("/admin/structure-definitions", new
        {
            name = "Other",
            levels = new object[] { new { id = otherLevel, name = "Part", parentLevelId = (Guid?)null } },
        });
        otherDefinition.StatusCode.Should().Be(HttpStatusCode.Created);
        var invalidTrees = new (string Field, object Nodes)[]
        {
            ("nodes[1].startSequence", new[] { Node(unitId, "A", 0, 60), Node(unitId, "B", 50, 99) }),
            ("nodes[0].children[0].startSequence", new[] { Node(unitId, "A", 0, 49, Node(sectionId, "A1", 40, 60)) }),
            ("nodes[0].children[0].levelId", new[] { Node(unitId, "A", 0, 49, Node(unitId, "A1", 0, 10)) }),
            ("nodes[0].levelId", new[] { Node(otherLevel, "A", 0, 49) }),
            ("nodes[0].endSequence", new[] { Node(unitId, "A", 0, 4000) }),
            ("nodes[0].startSequence", new[] { Node(unitId, "A", 30, 20) }),
        };
        var storedBefore = Canonical(reloaded["nodes"]!.AsArray());
        foreach (var (field, nodes) in invalidTrees)
        {
            var rejected = await client.PutAsJsonAsync($"/admin/document-parses/{parseId}/structure", new { nodes });
            rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest, field);
            var body = JsonNode.Parse(await rejected.Content.ReadAsStringAsync())!;
            body["errorCode"]!.GetValue<string>().Should().Be("DOCTHECA_STRUCTURE_INVALID");
            body["errors"]!.AsArray().Select(e => e!["field"]!.GetValue<string>()).Should().Contain(field);
            Canonical((await ReadData(await client.GetAsync($"/admin/document-parses/{parseId}/structure")))!["nodes"]!.AsArray())
                .Should().Equal(storedBefore, field);
        }

        // 7. Levels and definitions in use cannot be deleted; the document cannot switch definitions.
        var withoutSection = await client.PutAsJsonAsync($"/admin/structure-definitions/{definitionId}", new
        {
            name = "English textbook",
            levels = new object[] { new { id = unitId, name = "Unit", parentLevelId = (Guid?)null } },
        });
        withoutSection.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.DeleteAsync($"/admin/structure-definitions/{definitionId}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var otherDefinitionId = (await ReadData(otherDefinition))!["id"]!.GetValue<string>();
        (await client.PutAsJsonAsync($"/admin/document-files/{fileId}/metadata", new { structureDefinitionId = otherDefinitionId }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        // Renaming a used level is allowed.
        (await client.PutAsJsonAsync($"/admin/structure-definitions/{definitionId}", new
        {
            name = "English textbook",
            levels = new object[]
            {
                new { id = unitId, name = "Unit", parentLevelId = (Guid?)null },
                new { id = sectionId, name = "Part of unit", parentLevelId = unitId },
            },
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // 8. Deleting the parse deletes its nodes; then the definition is free again.
        (await client.DeleteAsync($"/admin/document-parses/{parseId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountNodesAsync(factory, parseId)).Should().Be(0);
        (await client.DeleteAsync($"/admin/structure-definitions/{definitionId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadData(await client.GetAsync($"/admin/document-files/{fileId}")))!["structureDefinitionId"].Should().BeNull();
    }

    [Fact]
    public async Task ThreeLevelDefinition_IsUsedOnAnotherDocument()
    {
        using var factory = Factory();
        using var client = Admin(factory);
        Guid chapter = Guid.NewGuid(), section = Guid.NewGuid(), subsection = Guid.NewGuid();
        var created = await client.PostAsJsonAsync("/admin/structure-definitions", new
        {
            name = "Paper",
            levels = new object[]
            {
                new { id = chapter, name = "Chapter", parentLevelId = (Guid?)null },
                new { id = section, name = "Section", parentLevelId = chapter },
                new { id = subsection, name = "Subsection", parentLevelId = section },
            },
        });
        var definitionId = (await ReadData(created))!["id"]!.GetValue<string>();
        var (fileId, parseId) = await SeedDocumentAsync(factory, 30);
        (await client.PutAsJsonAsync($"/admin/document-files/{fileId}/metadata", new { structureDefinitionId = definitionId }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var nodes = new[]
        {
            Node(chapter, "1 Introduction", 0, 14, Node(section, "1.1 Background", 2, 14, Node(subsection, "1.1.1 Scope", 5, 9))),
            Node(chapter, "2 Method", 15, 29),
        };
        (await client.PutAsJsonAsync($"/admin/document-parses/{parseId}/structure", new { nodes })).StatusCode.Should().Be(HttpStatusCode.OK);
        var tree = (await ReadData(await client.GetAsync($"/admin/document-parses/{parseId}/structure")))!;
        tree["nodes"]![0]!["children"]![0]!["children"]![0]!["levelName"]!.GetValue<string>().Should().Be("Subsection");
        Canonical(tree["nodes"]!.AsArray()).Should().Equal(Canonical(JsonSerializer.SerializeToNode(nodes)!.AsArray()));

        // Clearing the tree frees the document to switch definitions.
        (await client.PutAsJsonAsync($"/admin/document-parses/{parseId}/structure", new { nodes = Array.Empty<object>() })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PutAsJsonAsync($"/admin/document-files/{fileId}/metadata", new { structureDefinitionId = "" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ConcurrentSaves_NeverProduceAMixedTree()
    {
        using var factory = Factory();
        using var client = Admin(factory);
        Guid top = Guid.NewGuid(), child = Guid.NewGuid();
        var definitionId = (await ReadData(await client.PostAsJsonAsync("/admin/structure-definitions", new
        {
            name = "Concurrency",
            levels = new object[] { new { id = top, name = "A", parentLevelId = (Guid?)null }, new { id = child, name = "B", parentLevelId = top } },
        })))!["id"]!.GetValue<string>();
        var (fileId, parseId) = await SeedDocumentAsync(factory, 200);
        await client.PutAsJsonAsync($"/admin/document-files/{fileId}/metadata", new { structureDefinitionId = definitionId });

        for (var round = 0; round < 5; round++)
        {
            var left = Enumerable.Range(0, 10).Select(i => Node(top, $"left-{round}-{i}", i * 20, i * 20 + 19, Node(child, $"lc-{i}", i * 20, i * 20 + 9))).ToArray();
            var right = Enumerable.Range(0, 4).Select(i => Node(top, $"right-{round}-{i}", i * 50, i * 50 + 49)).ToArray();
            var responses = await Task.WhenAll(
                client.PutAsJsonAsync($"/admin/document-parses/{parseId}/structure", new { nodes = left }),
                client.PutAsJsonAsync($"/admin/document-parses/{parseId}/structure", new { nodes = right }));
            responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);

            var stored = Canonical((await ReadData(await client.GetAsync($"/admin/document-parses/{parseId}/structure")))!["nodes"]!.AsArray());
            var expectedLeft = Canonical(JsonSerializer.SerializeToNode(left)!.AsArray());
            var expectedRight = Canonical(JsonSerializer.SerializeToNode(right)!.AsArray());
            (stored.SequenceEqual(expectedLeft) || stored.SequenceEqual(expectedRight)).Should().BeTrue("the last complete save wins");
        }
    }

    // ---------- helpers ----------

    private static object Node(Guid levelId, string name, int start, int end, params object[] children) =>
        new { levelId, name, startSequence = start, endSequence = end, children };

    private static object[] TextbookTree(Guid unit, Guid section) =>
        Enumerable.Range(0, 8).Select(u => Node(unit, $"Unit {u + 1}", u * 50, u * 50 + 49,
            Node(section, "Section A", u * 50, u * 50 + 19),
            Node(section, "Section B", u * 50 + 20, u * 50 + 39),
            Node(section, "Project", u * 50 + 40, u * 50 + 49))).ToArray();

    /// <summary>Level, name and range of every node, pre-order with depth; ids are server-assigned.</summary>
    private static List<string> Canonical(JsonArray nodes, int depth = 0)
    {
        var result = new List<string>();
        foreach (var node in nodes.OrderBy(n => n!["startSequence"]!.GetValue<int>()))
        {
            result.Add($"{depth}|{node!["levelId"]!.GetValue<string>()}|{node["name"]!.GetValue<string>()}|{node["startSequence"]}|{node["endSequence"]}");
            result.AddRange(Canonical(node["children"]?.AsArray() ?? [], depth + 1));
        }
        return result;
    }

    private static async Task<JsonNode?> ReadData(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        return JsonNode.Parse(body)!["data"];
    }

    private static async Task<(string FileId, string ParseId)> SeedDocumentAsync(WebApplicationFactory<Program> factory, int blockCount)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var file = new DocumentFileEntity { Id = Guid.NewGuid(), FileName = $"book-{Guid.NewGuid():N}.pdf", ContentType = "application/pdf" };
        var parse = new DocumentParseEntity { Id = Guid.NewGuid(), DocumentFileId = file.Id, Status = "parsed" };
        context.DocumentFiles.Add(file);
        context.DocumentParses.Add(parse);
        context.DocumentParseBlocks.AddRange(Enumerable.Range(0, blockCount).Select(i => new DocumentParseBlockEntity
        {
            ParseId = parse.Id,
            PageId = i / 10,
            SortIndex = i % 10,
            Sequence = i,
            BlockType = "text",
            TextContent = $"block {i}",
            TextFormat = "plain",
            BlockData = $"{{\"sequence\":{i}}}",
        }));
        await context.SaveChangesAsync();
        return (file.Id.ToString(), parse.Id.ToString());
    }

    private static async Task<int> CountNodesAsync(WebApplicationFactory<Program> factory, string parseId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var id = Guid.Parse(parseId);
        return await context.DocumentStructureNodes.CountAsync(n => n.ParseId == id);
    }

    /// <summary>Records re-index calls; reports success like a healthy OpenSearch.</summary>
    private sealed class RecordingSearchIndex : ISearchIndexService
    {
        public List<Guid> IndexedParses { get; } = [];
        public Task EnsureIndexAsync() => Task.CompletedTask;
        public Task<bool> IndexParseBlocksAsync(Guid parseId, Guid documentFileId, string fileName, string? subject, string? grade, string? year)
        {
            lock (IndexedParses) IndexedParses.Add(parseId);
            return Task.FromResult(true);
        }
        public Task DeleteParseIndexAsync(Guid parseId) => Task.CompletedTask;
        public Task DeleteDocumentFileIndexAsync(Guid documentFileId) => Task.CompletedTask;
        public Task UpdateDocumentFileMetadataAsync(Guid documentFileId, string? subject, string? grade, string? year) => Task.CompletedTask;
        public Task<(List<Doctheca.Domain.Models.SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
            string query, bool phrase, Doctheca.Domain.Models.SearchFilterModel? filter, int pageSize, string? pageToken) =>
            Task.FromResult((new List<Doctheca.Domain.Models.SearchResultModel>(), 0, (string?)null));
    }
}
