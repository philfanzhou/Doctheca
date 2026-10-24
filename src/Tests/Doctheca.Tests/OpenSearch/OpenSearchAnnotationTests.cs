using System.Text.Json;
using Doctheca.Domain.Models;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;
using Doctheca.Service;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Doctheca.Tests.OpenSearch;

[Collection(OpenSearchCollection.Name)]
public sealed class OpenSearchAnnotationTests(OpenSearchFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Annotations_AreSearchableOncePerBlock_WithStableFiltersCursorAndSnippetRules(bool phrase)
    {
        var parse = Guid.NewGuid();
        var otherParse = Guid.NewGuid();
        var file = Guid.NewGuid();
        var blocks = new List<DocumentParseBlockModel>
        {
            Block(parse, 0, null, "running quartz lantern\n图题", null, "image"),
            Block(parse, 1, null, null, "orbital cobalt signal", "table"),
            Block(parse, 2, "bodyonly nebula", null, null),
            Block(parse, 3, "unified comet", "unified comet", "unified comet"),
            Block(parse, 4, "alpha", "omega", null),
            Block(parse, 5, "distant harbour", "bright copper", "silver willow"),
            Block(parse, 6, " \t", "", "\r\n"),
            Block(otherParse, 0, null, "running quartz lantern", "orbital cobalt signal", "image")
        };
        var name = "doctheca-annotations-" + Guid.NewGuid().ToString("N");
        using var services = Repositories(blocks);
        var index = Index(name, services);
        try
        {
            await index.EnsureIndexAsync();
            (await Task.WhenAll(
                index.IndexParseBlocksAsync(parse, file, "same.pdf", "math", "g7", "2025"),
                index.IndexParseBlocksAsync(otherParse, Guid.NewGuid(), "same.pdf", "other", "g8", "2026")))
                .Should().OnlyContain(success => success);
            await RefreshAsync(name);
            var filter = new SearchFilterModel { ParseId = parse };
            var caption = await HitsAsync(index, phrase ? "quartz lantern" : "running", phrase, filter, blocks[0]);
            caption.Single().AssociatedText.Should().Contain("<em>");
            caption.Single().Caption.Should().Be(blocks[0].Caption);
            var footnote = await HitsAsync(index, phrase ? "cobalt signal" : "cobalt", phrase, filter, blocks[1]);
            footnote.Single().AssociatedText.Should().Contain("<em>");
            footnote.Single().Footnote.Should().Be(blocks[1].Footnote);
            await HitsAsync(index, phrase ? "bodyonly nebula" : "bodyonly", phrase, filter, blocks[2]);
            var shared = await HitsAsync(index, phrase ? "unified comet" : "unified", phrase, filter, blocks[3]);
            shared.Single().AssociatedText.Should().Contain("<em>");
            // A nonmatching body cannot hide the matching caption or footnote snippet.
            var captionWithBody = await HitsAsync(index, phrase ? "bright copper" : "copper", phrase, filter, blocks[5]);
            captionWithBody.Single().AssociatedText.Should().Contain("<em>").And.NotContain("harbour");
            var noteWithBody = await HitsAsync(index, phrase ? "silver willow" : "willow", phrase, filter, blocks[5]);
            noteWithBody.Single().AssociatedText.Should().Contain("<em>").And.NotContain("harbour").And.NotContain("copper");
            await HitsAsync(index, "alpha omega", true, filter);

            // Real stemming remains available for annotation text.
            if (!phrase) await HitsAsync(index, "run", false, filter, blocks[0]);
            await HitsAsync(index, "quartz", false, new SearchFilterModel
            {
                ParseId = parse, DocumentFileId = file, Subject = "math", Grade = "g7", Year = "2025",
                DocumentTitle = "same.pdf", BlockType = "image", PageNumber = 0, HasImage = true,
                BlockSubType = "synthetic", TextLevel = -1, TextFormat = "plain"
            }, blocks[0]);
            await HitsAsync(index, "quartz", false, new SearchFilterModel { ParseId = parse, Subject = "other" });
            await HitsAsync(index, "quartz", false, new SearchFilterModel { ParseId = otherParse }, blocks[7]);

            // Stable block_id sorting and search_after still produce every hit exactly once.
            var first = await index.ExactSearchAsync("quartz", false, null, 1, null);
            first.TotalCount.Should().Be(2);
            first.NextToken.Should().NotBeNull();
            var second = await index.ExactSearchAsync("quartz", false, null, 1, first.NextToken);
            first.Results.Concat(second.Results).Select(hit => hit.SegmentId).Should()
                .BeEquivalentTo(new[] { blocks[0].Id.ToString(), blocks[7].Id.ToString() });

            using var http = new HttpClient();
            using var raw = JsonDocument.Parse(await http.GetStringAsync($"{fixture.Url}/{name}/_count"));
            raw.RootElement.GetProperty("count").GetInt32().Should().Be(7); // Empty-only block was not indexed.
        }
        finally { await DeleteAsync(name); }
    }

    [Fact]
    public async Task Mapping3Upgrade_RecreatesDerivedIndex_AndExplicitReindexRestoresRecall()
    {
        var name = "doctheca-annotation-version-" + Guid.NewGuid().ToString("N");
        var parse = Guid.NewGuid();
        var block = Block(parse, 0, null, "versionprobe caption", "versionprobe footnote");
        using var services = Repositories([block]);
        var index = Index(name, services);
        using var http = new HttpClient();
        try
        {
            // Reproduce the previous mapping's version: startup must discard the derived index.
            using (var create = await http.PutAsync($"{fixture.Url}/{name}", new StringContent(
                       "{\"mappings\":{\"_meta\":{\"mapping_version\":3}}}", System.Text.Encoding.UTF8, "application/json")))
                create.EnsureSuccessStatusCode();
            using (var seed = await http.PutAsync($"{fixture.Url}/{name}/_doc/legacy?refresh=true", new StringContent(
                       "{\"text\":\"legacy derived document\"}", System.Text.Encoding.UTF8, "application/json")))
                seed.EnsureSuccessStatusCode();
            await index.EnsureIndexAsync();
            using (var mapping = JsonDocument.Parse(await http.GetStringAsync($"{fixture.Url}/{name}/_mapping")))
                mapping.RootElement.GetProperty(name).GetProperty("mappings").GetProperty("_meta").GetProperty("mapping_version").GetInt32().Should().Be(4);
            (await index.ExactSearchAsync("versionprobe", false, null, 10, null)).Results.Should().BeEmpty();
            (await index.IndexParseBlocksAsync(parse, Guid.NewGuid(), "version.pdf", null, null, null)).Should().BeTrue();
            await RefreshAsync(name);
            await HitsAsync(index, "versionprobe", false, null, block);

        }
        finally { await DeleteAsync(name); }
    }

    [Fact]
    public async Task FailedQuery_PreservesEmptyResultDegradation_WithoutAnnotationDiagnostics()
    {
        using var services = Repositories([]);
        var index = Index("doctheca-missing-" + Guid.NewGuid().ToString("N"), services);
        var logs = new Mock<Microsoft.Extensions.Logging.ILogger<SearchDomainService>>();
        var domain = new SearchDomainService(index, logs.Object);
        var result = await domain.ExactSearchAsync("synthetic annotation canary", true, null, 10, null);
        result.Results.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
        result.NextToken.Should().BeNull();
        logs.Invocations.Should().ContainSingle(invocation => invocation.Method.Name == "Log");
        var logged = logs.Invocations.Single(invocation => invocation.Method.Name == "Log");
        logged.Arguments[2]!.ToString().Should().Be("OpenSearch query failed; returning empty results");
        logged.Arguments[3].Should().BeNull();
    }

    private OpenSearchIndexService Index(string name, IServiceProvider services) => new(
        Options.Create(new OpenSearchOptions { Url = fixture.Url, IndexName = name }), services, NullLogger<OpenSearchIndexService>.Instance);

    private static ServiceProvider Repositories(List<DocumentParseBlockModel> blocks)
    {
        var repository = new Mock<IDocumentParseBlockRepository>();
        repository.Setup(value => value.GetByParseIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid parse) => blocks.Where(block => block.ParseId == parse).ToList());
        var structure = new Mock<IDocumentStructureRepository>();
        structure.Setup(value => value.GetCurrentBindingsAsync(It.IsAny<Guid>())).ReturnsAsync([]);
        return new ServiceCollection().AddSingleton(repository.Object).AddSingleton(structure.Object).BuildServiceProvider();
    }

    private static DocumentParseBlockModel Block(Guid parse, int sequence, string? text, string? caption, string? footnote, string type = "text") => new()
    {
        Id = Guid.NewGuid(), ParseId = parse, Sequence = sequence, PageId = 0, SortIndex = sequence,
        BlockType = type, TextContent = text, Caption = caption, Footnote = footnote,
        ImageId = type == "image" ? Guid.NewGuid() : null, SubType = "synthetic", TextFormat = "plain", BlockData = "{}"
    };

    private static async Task<List<SearchResultModel>> HitsAsync(OpenSearchIndexService index, string query, bool phrase,
        SearchFilterModel? filter, params DocumentParseBlockModel[] expected)
    {
        var (hits, total, _) = await index.ExactSearchAsync(query, phrase, filter, 100, null);
        total.Should().Be(expected.Length);
        hits.Select(hit => hit.SegmentId).Should().BeEquivalentTo(expected.Select(block => block.Id.ToString()));
        foreach (var hit in hits)
        {
            hit.StartOffset.Should().Be(0);
            hit.EndOffset.Should().Be(0);
        }
        return hits;
    }

    private async Task RefreshAsync(string name)
    {
        using var http = new HttpClient();
        using var response = await http.PostAsync($"{fixture.Url}/{name}/_refresh", null);
        response.EnsureSuccessStatusCode();
    }

    private async Task DeleteAsync(string name)
    {
        using var http = new HttpClient();
        using var response = await http.DeleteAsync($"{fixture.Url}/{name}");
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
    }
}
