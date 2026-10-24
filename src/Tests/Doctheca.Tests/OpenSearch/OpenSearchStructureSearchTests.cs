using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Doctheca.Domain.Models;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Repositories;
using Doctheca.Service;
using Xunit;

namespace Doctheca.Tests.OpenSearch;

/// <summary>A single-node OpenSearch with the security plugin disabled, shared by the collection.</summary>
public sealed class OpenSearchFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("opensearchproject/opensearch:2.19.1")
        .WithEnvironment("discovery.type", "single-node")
        .WithEnvironment("DISABLE_SECURITY_PLUGIN", "true")
        .WithEnvironment("DISABLE_INSTALL_DEMO_CONFIG", "true")
        .WithEnvironment("OPENSEARCH_JAVA_OPTS", "-Xms512m -Xmx512m")
        .WithPortBinding(9200, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
            .ForPort(9200).ForPath("/_cluster/health")))
        .Build();

    public string Url => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(9200)}";

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OpenSearchCollection : ICollectionFixture<OpenSearchFixture>
{
    public const string Name = "opensearch-integration";
}

/// <summary>
/// Issue #89 against a real OpenSearch: after a tree is indexed, the structureNodeId filter of a
/// child node returns only hits inside it, the parent's filter returns hits from all its
/// children, every hit carries its structurePath, and searching without the filter returns
/// exactly the same hits as before any structure existed.
/// </summary>
[Collection(OpenSearchCollection.Name)]
public sealed class OpenSearchStructureSearchTests(OpenSearchFixture fixture)
{
    private static readonly Guid ParseId = Guid.NewGuid();
    private static readonly Guid FileId = Guid.NewGuid();

    [Fact]
    public async Task StructureFilter_SelectsSubtrees_AndUnfilteredHitsAreUnchanged()
    {
        var indexName = "doctheca-structure-" + Guid.NewGuid().ToString("N");
        var blocks = Enumerable.Range(0, 120).Select(i => new DocumentParseBlockModel
        {
            Id = Guid.NewGuid(),
            ParseId = ParseId,
            PageId = i / 10,
            SortIndex = i % 10,
            Sequence = i,
            BlockType = "text",
            // Every fifth block mentions the probe word.
            TextContent = i % 5 == 0 ? $"the changing world block {i}" : $"filler {i}",
            BlockData = "{}",
        }).ToList();
        var nodes = new List<DocumentStructureNodeModel>();
        var store = new FakeRepositories(blocks, nodes);
        var services = new ServiceCollection()
            .AddScoped<IDocumentParseBlockRepository>(_ => store)
            .AddScoped<IDocumentStructureRepository>(_ => store)
            .BuildServiceProvider();
        var index = new OpenSearchIndexService(
            Options.Create(new OpenSearchOptions { Url = fixture.Url, IndexName = indexName }),
            services, NullLogger<OpenSearchIndexService>.Instance);
        await index.EnsureIndexAsync();

        // Before any structure: the baseline hits.
        (await index.IndexParseBlocksAsync(ParseId, FileId, "book.pdf", null, null, null)).Should().BeTrue();
        await RefreshAsync(indexName);
        var before = await SearchAsync(index, null);
        before.Should().HaveCount(24);
        before.Should().OnlyContain(hit => hit.StructurePath == null);

        // Three units of 40 blocks, each with sections of 20.
        for (var u = 0; u < 3; u++)
        {
            var unit = new DocumentStructureNodeModel { Id = Guid.NewGuid(), ParseId = ParseId, Name = $"Unit {u + 1}", StartSequence = u * 40, EndSequence = u * 40 + 39 };
            nodes.Add(unit);
            nodes.Add(new DocumentStructureNodeModel { Id = Guid.NewGuid(), ParseId = ParseId, ParentId = unit.Id, Name = "Section A", StartSequence = u * 40, EndSequence = u * 40 + 19 });
            nodes.Add(new DocumentStructureNodeModel { Id = Guid.NewGuid(), ParseId = ParseId, ParentId = unit.Id, Name = "Section B", StartSequence = u * 40 + 20, EndSequence = u * 40 + 39 });
        }
        (await index.IndexParseBlocksAsync(ParseId, FileId, "book.pdf", null, null, null)).Should().BeTrue();
        await RefreshAsync(indexName);

        var after = await SearchAsync(index, null);
        after.Select(hit => hit.SegmentId).Should().BeEquivalentTo(before.Select(hit => hit.SegmentId));
        after.Should().OnlyContain(hit => hit.StructurePath != null && hit.StructurePath.StartsWith("Unit "));

        var unit2 = nodes.Single(n => n.Name == "Unit 2");
        var unit2SectionB = nodes.Single(n => n.ParentId == unit2.Id && n.Name == "Section B");
        var sequenceOf = blocks.ToDictionary(b => b.Id.ToString(), b => b.Sequence!.Value);

        var sectionHits = await SearchAsync(index, unit2SectionB.Id);
        sectionHits.Select(hit => sequenceOf[hit.SegmentId]).Should().BeEquivalentTo([60, 65, 70, 75]);
        sectionHits.Should().OnlyContain(hit => hit.StructurePath == "Unit 2 > Section B");

        var unitHits = await SearchAsync(index, unit2.Id);
        unitHits.Select(hit => sequenceOf[hit.SegmentId]).Should().BeEquivalentTo([40, 45, 50, 55, 60, 65, 70, 75]);
        unitHits.Select(hit => hit.StructurePath).Distinct().Should().BeEquivalentTo(["Unit 2 > Section A", "Unit 2 > Section B"]);

        // Removing the structure and re-indexing clears the fields again.
        nodes.Clear();
        (await index.IndexParseBlocksAsync(ParseId, FileId, "book.pdf", null, null, null)).Should().BeTrue();
        await RefreshAsync(indexName);
        (await SearchAsync(index, unit2.Id)).Should().BeEmpty();
        (await SearchAsync(index, null)).Should().OnlyContain(hit => hit.StructurePath == null);
    }

    private static async Task<List<SearchResultModel>> SearchAsync(OpenSearchIndexService index, Guid? nodeId)
    {
        var filter = nodeId is null ? null : new SearchFilterModel { StructureNodeId = nodeId };
        var (results, _, _) = await index.ExactSearchAsync("changing", phrase: false, filter, pageSize: 100, pageToken: null);
        return results;
    }

    private async Task RefreshAsync(string indexName)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{fixture.Url}/{indexName}/_refresh", null)).EnsureSuccessStatusCode();
    }

    private sealed class FakeRepositories(List<DocumentParseBlockModel> blocks, List<DocumentStructureNodeModel> nodes)
        : IDocumentParseBlockRepository, IDocumentStructureRepository
    {
        public Task<List<DocumentParseBlockModel>> GetByParseIdAsync(Guid parseId) => Task.FromResult(blocks.ToList());
        public Task<List<DocumentStructureNodeModel>> GetCurrentBindingsAsync(Guid parseId) => Task.FromResult(nodes.Where(n => n.ParseId == parseId).ToList());
        Task<List<DocumentStructureNodeModel>> IDocumentStructureRepository.GetByParseIdAsync(Guid parseId) => Task.FromResult(nodes.ToList());
        public Task AddBlocksAsync(Guid parseId, IEnumerable<DocumentParseBlockModel> items) => throw new NotSupportedException();
        public Task<List<DocumentParseBlockModel>> GetByParseAndPageAsync(Guid parseId, int pageId) => throw new NotSupportedException();
        public Task<List<DocumentParseBlockModel>> GetPageBySequenceAsync(Guid parseId, int? afterSequence, int limit, int? fromSequence = null, int? toSequence = null) => throw new NotSupportedException();
        public Task<HashSet<int>> GetSequencesAsync(Guid parseId) => throw new NotSupportedException();
        public Task<bool> ReplaceAsync(Guid parseId, IReadOnlyList<DocumentStructureNodeModel> items) => throw new NotSupportedException();
        public Task<HashSet<Guid>> GetForeignNodeIdsAsync(Guid parseId, IReadOnlyCollection<Guid> nodeIds) => throw new NotSupportedException();
        public Task<bool> DocumentFileHasNodesAsync(Guid documentFileId) => throw new NotSupportedException();
    }
}
