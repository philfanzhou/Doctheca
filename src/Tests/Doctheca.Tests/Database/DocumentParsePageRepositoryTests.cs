using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Database.Repositories;
using Doctheca.Domain.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Doctheca.Tests.Database;

[Collection(MigrationIntegrationCollection.Name)]
public sealed class DocumentParsePageRepositoryTests(PostgreSqlMigrationFixture fixture) : IAsyncLifetime
{
    private string _database = null!;
    private static readonly Guid Parse = MigrationGoldenStates.ParseId;
    private readonly Guid _otherParse = Guid.NewGuid();
    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync();
        using var context = fixture.CreateContext(_database);
        await new MigrationExecutor(context).ExecuteAsync();
        await MigrationGoldenStates.SeedBusinessRowsAsync(context);
        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO document_parses (id,document_file_id,model_version,status)
            VALUES ({_otherParse},{MigrationGoldenStates.FileId},'vlm','parsing')
            """);
    }
    public Task DisposeAsync() => fixture.DropDatabaseAsync(_database);

    [Fact]
    public async Task ReadBatch_ReturnsOnlyTheExactRequestedPages()
    {
        using var context = fixture.CreateContext(_database);
        DocumentParsePage[] pages = [new(Parse, 1, "iv", "pdf-page-labels"), new(Parse, 2, "iv", "pdf-page-labels"),
            new(Parse, 3, "封面\n一", "page-number-blocks"), new(Parse, 4, null, null), new(Parse, 5, "", "")];
        context.DocumentParsePages.AddRange(pages.Select(page => new DocumentParsePageEntity
        {
            ParseId = page.ParseId, PageNumber = page.PageNumber, Label = page.Label, LabelSource = page.LabelSource
        }));
        context.DocumentParsePages.AddRange(
            new DocumentParsePageEntity { ParseId = _otherParse, PageNumber = 1, Label = "foreign" },
            new DocumentParsePageEntity { ParseId = _otherParse, PageNumber = 2, Label = "other" });
        await context.SaveChangesAsync();

        var repository = new DocumentParsePageRepository(context);
        var result = await repository.ReadBatchAsync([.. pages.Select(page => new DocumentParsePageKey(Parse, page.PageNumber)), new(Parse, 99)]);
        result.Values.OrderBy(page => page.PageNumber).Should().Equal(pages);
        // Different parse and page lists must not turn into a cross-product in the result.
        (await repository.ReadBatchAsync([new(Parse, 1), new(_otherParse, 2)])).Values.Should().HaveCount(2)
            .And.Contain(new DocumentParsePage(Parse, 1, "iv", "pdf-page-labels"))
            .And.Contain(new DocumentParsePage(_otherParse, 2, "other", null));
        (await repository.ReadBatchAsync([new(Guid.NewGuid(), 1)])).Should().BeEmpty();
        (await repository.ReadBatchAsync([])).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParseAndDocumentDeletion_CascadePages(bool document)
    {
        using var context = fixture.CreateContext(_database);
        context.DocumentParsePages.Add(new DocumentParsePageEntity { ParseId = Parse, PageNumber = 1, Label = "label" });
        await context.SaveChangesAsync();
        var repository = new DocumentParsePageRepository(context);
        if (document) await context.DocumentFiles.Where(file => file.Id == MigrationGoldenStates.FileId).ExecuteDeleteAsync();
        else await context.DocumentParses.Where(parse => parse.Id == Parse).ExecuteDeleteAsync();
        (await repository.ReadBatchAsync([new(Parse, 1)])).Should().BeEmpty();
    }
}
