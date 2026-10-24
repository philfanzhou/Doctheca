using System.Text.Json;
using Doctheca.Database;
using Doctheca.Database.Repositories;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Service;
using Doctheca.Tests.Database;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Doctheca.Tests.OpenSearch;

[Collection(OpenSearchCollection.Name)]
public sealed class OpenSearchHeadingClassificationTests(OpenSearchFixture fixture)
{
    [Fact]
    public async Task ReindexSameDatabaseIds_SeparatesCanonicalAndLegacyLevels_WithoutMappingChangeOrEmptyHits()
    {
        var postgres = new PostgreSqlMigrationFixture();
        await postgres.InitializeAsync();
        var database = await postgres.CreateDatabaseAsync();
        var name = "doctheca-headings-" + Guid.NewGuid().ToString("N");
        using var http = new HttpClient();
        try
        {
            using var context = postgres.CreateContext(database);
            await context.GetService<IMigrator>().MigrateAsync(DocthecaMigrationExecutor.AddBlockFootnotesMigrationId);
            await PageMigrationTests.SeedAllEarlierTablesAsync(context);
            var rows = new[]
            {
                Row("title", null, -1, -2), Row("text", null, -1, -1),
                Row("title", "heading-2", 2, 2), Row("text", null, 0, 0),
                Row("title", null, 3, 3), Row("title", "heading-" + new string('0',42), 4, 4),
                Row("title", "heading-2", 0, 0), Row("title", null, -1, -2, null),
                Row("title", null, -1, -2, null, "running beacon delta")
            };
            var sequence = 100;
            foreach (var row in rows)
                await context.Database.ExecuteSqlAsync($"""
                    INSERT INTO document_parse_blocks (id,parse_id,page_id,sort_index,sequence,block_type,
                        text_content,block_data,created_at,sub_type,text_level,text_format,caption)
                    VALUES ({row.Id},{MigrationGoldenStates.ParseId},0,{sequence},{sequence++},{row.Type},
                        {row.Text},{"{}"}::jsonb,'2026-01-01T00:00:00Z',{row.Subtype},{row.Before},'plain',{row.Caption});
                    """);
            using var services = new ServiceCollection()
                .AddDbContext<DocthecaDbContext>(options => options.UseNpgsql(postgres.GetConnectionString(database)))
                .AddScoped<IDocumentParseBlockRepository, DocumentParseBlockRepository>()
                .AddSingleton(Moq.Mock.Of<IDocumentStructureRepository>(repository => repository.GetByParseIdAsync(Moq.It.IsAny<Guid>()) == Task.FromResult(new List<Doctheca.Domain.Models.Structure.DocumentStructureNodeModel>()))).BuildServiceProvider();
            var index = new OpenSearchIndexService(Options.Create(new OpenSearchOptions { Url=fixture.Url, IndexName=name }),
                services, NullLogger<OpenSearchIndexService>.Instance);
            await index.EnsureIndexAsync();
            await ReindexAsync();
            (await HitsAsync(-1, "beacon delta", true)).Should().Contain(rows[0].Id);
            await new DocthecaMigrationExecutor(context).ExecuteAsync();
            // The existing index is deliberately stale until the documented explicit reindex.
            (await HitsAsync(-1, "beacon delta", true)).Should().Contain(rows[0].Id);
            await index.EnsureIndexAsync(); // mapping4 stays intact; classification alone cannot recreate it.
            await ReindexAsync();
            foreach (var level in new[] {-2,-1,0,2,3,4})
            foreach (var phrase in new[] {false,true})
            {
                var actual = await HitsAsync(level, phrase ? "beacon delta" : "run", phrase);
                actual.Should().BeEquivalentTo(rows.Where(row => row.After==level && (row.Text is not null || row.Caption is not null))
                    .Select(row => row.Id));
            }
            using var mapping = JsonDocument.Parse(await http.GetStringAsync($"{fixture.Url}/{name}/_mapping"));
            mapping.RootElement.GetProperty(name).GetProperty("mappings").GetProperty("_meta")
                .GetProperty("mapping_version").GetInt32().Should().Be(4);
            var unfiltered = await index.ExactSearchAsync("beacon delta", true,
                new SearchFilterModel { ParseId=MigrationGoldenStates.ParseId }, 100, null);
            unfiltered.TotalCount.Should().Be(8);
            unfiltered.Results.Select(hit=>hit.SegmentId).Distinct().Should().HaveCount(8);
            unfiltered.Results.Should().NotContain(hit=>hit.SegmentId==rows[7].Id.ToString());

            async Task ReindexAsync()
            {
                (await index.IndexParseBlocksAsync(MigrationGoldenStates.ParseId,MigrationGoldenStates.FileId,
                    "synthetic.pdf",null,null,null)).Should().BeTrue();
                using var refresh = await http.PostAsync($"{fixture.Url}/{name}/_refresh",null);
                refresh.EnsureSuccessStatusCode();
            }
            async Task<Guid[]> HitsAsync(int level,string query,bool phrase)
            {
                var response = await index.ExactSearchAsync(query,phrase,
                    new SearchFilterModel { ParseId=MigrationGoldenStates.ParseId,TextLevel=level },100,null);
                response.Results.Should().OnlyContain(hit=>hit.TextLevel==level);
                response.Results.Should().OnlyContain(hit=>hit.MatchType==(phrase?SearchMatchType.ExactPhrase:SearchMatchType.Stemmed));
                return response.Results.Select(hit=>Guid.Parse(hit.SegmentId)).ToArray();
            }
        }
        finally
        {
            using var deleted = await http.DeleteAsync($"{fixture.Url}/{name}");
            await postgres.DropDatabaseAsync(database);
            await postgres.DisposeAsync();
        }
    }

    private static HeadingRow Row(string type,string? subtype,int before,int after,
        string? text="running beacon delta",string? caption=null) => new(Guid.NewGuid(),type,subtype,before,after,text,caption);
    private sealed record HeadingRow(Guid Id,string Type,string? Subtype,int Before,int After,string? Text,string? Caption);
}
