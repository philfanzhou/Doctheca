using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Database.Repositories;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Service;
using Doctheca.Tests.Database;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Doctheca.Tests.OpenSearch;

[Collection(OpenSearchCollection.Name)]
public sealed class CurrentBindingIndexTests(OpenSearchFixture fixture)
{
    [Fact]
    public async Task CurrentBindingOverridesHistoryForIndexing_AndUnbindRebindRemovesOldMembership()
    {
        var postgres = new PostgreSqlMigrationFixture(); await postgres.InitializeAsync(); var database = await postgres.CreateDatabaseAsync();
        var name = "doctheca-current-binding-" + Guid.NewGuid().ToString("N"); using var http = new HttpClient();
        try
        {
            using var db = postgres.CreateContext(database); await db.Database.EnsureCreatedAsync();
            var definition = new StructureDefinitionEntity { Name = "Custom" }; db.StructureDefinitions.Add(definition);
            var level = new StructureLevelEntity { DefinitionId = definition.Id, Name = "Root" }; db.StructureLevels.Add(level);
            var file = new DocumentFileEntity { FileName = "Book", StructureDefinitionId = definition.Id, StructaDocDocumentId = Guid.NewGuid() }; db.DocumentFiles.Add(file);
            var parse = new DocumentParseEntity { DocumentFileId = file.Id, Status = DocumentParseStatus.Parsed }; db.DocumentParses.Add(parse);
            var historical = new DocumentStructureTreeEntity { DocumentFileId = file.Id, OriginParseId = parse.Id }; db.DocumentStructureTrees.Add(historical);
            var historicalNode = new DocumentStructureNodeEntity { DocumentFileId = file.Id, TreeId = historical.Id, ParseId = parse.Id, LevelId = level.Id, Name = "History", StartSequence = 0, EndSequence = 1 }; db.DocumentStructureNodes.Add(historicalNode);
            db.DocumentParseBlocks.AddRange(Enumerable.Range(0, 2).Select(i => new DocumentParseBlockEntity { ParseId = parse.Id, Sequence = i, BlockType = "text", TextContent = "binding beacon", BlockData = "{}" }));
            await db.SaveChangesAsync();
            var repo = new DocumentAuthoringRepository(db); var chapter = Guid.NewGuid();
            (await repo.SaveTreeAsync(file.Id, [new(chapter, level.Id, "Current chapter", parse.Id, 0, 1, [])], default)).Value!.Tree.TreeId.Should().NotBe(historical.Id);
            using var services = new ServiceCollection().AddDbContext<DocthecaDbContext>(o => o.UseNpgsql(postgres.GetConnectionString(database)))
                .AddScoped<IDocumentParseBlockRepository, DocumentParseBlockRepository>().AddScoped<IDocumentStructureRepository, DocumentStructureRepository>().BuildServiceProvider();
            var index = new OpenSearchIndexService(Options.Create(new OpenSearchOptions { Url = fixture.Url, IndexName = name }), services, NullLogger<OpenSearchIndexService>.Instance);
            await index.EnsureIndexAsync(); await Reindex();
            var filter = new SearchFilterModel { StructureNodeId = chapter };
            var hits = await index.ExactSearchAsync("binding beacon", true, filter, 20, null);
            hits.Results.Should().HaveCount(2).And.OnlyContain(h => h.StructurePath == "Current chapter");
            (await index.ExactSearchAsync("binding beacon", true, new() { StructureNodeId = historicalNode.Id }, 20, null)).Results.Should().BeEmpty();
            (await new DocumentStructureRepository(db).GetByParseIdAsync(parse.Id)).Single().Name.Should().Be("History");
            (await new InternalStructureRepository(db.Options()).ReadContentAsync(chapter, new(null, 10, null), default)).Page!.Items.Should().HaveCount(2);
            var unbound = await repo.SaveTreeAsync(file.Id, [new(chapter, level.Id, "Current chapter", null, null, null, [])], default);
            unbound.Value!.AffectedParseIds.Should().Contain(parse.Id); await Reindex();
            (await index.ExactSearchAsync("binding beacon", true, filter, 20, null)).Results.Should().BeEmpty();
            (await index.ExactSearchAsync("binding beacon", true, null, 20, null)).Results.Should().HaveCount(2).And.OnlyContain(h => h.StructurePath == null);
            var renamed = Guid.NewGuid(); await repo.SaveTreeAsync(file.Id, [new(renamed, level.Id, "New current", parse.Id, 0, 1, [])], default); await Reindex();
            (await index.ExactSearchAsync("binding beacon", true, filter, 20, null)).Results.Should().BeEmpty();
            (await index.ExactSearchAsync("binding beacon", true, new() { StructureNodeId = renamed }, 20, null)).Results.Should().HaveCount(2).And.OnlyContain(h => h.StructurePath == "New current");
            (await new DocumentStructureRepository(db).GetByParseIdAsync(parse.Id)).Single().Id.Should().Be(historicalNode.Id);
            async Task Reindex() { (await index.IndexParseBlocksAsync(parse.Id, file.Id, "Book", null, null, null)).Should().BeTrue(); (await http.PostAsync(fixture.Url + "/" + name + "/_refresh", null)).EnsureSuccessStatusCode(); }
        }
        finally { await http.DeleteAsync(fixture.Url + "/" + name); await postgres.DropDatabaseAsync(database); await postgres.DisposeAsync(); }
    }
}
