using System.Data.Common;
using Doctheca.Database;
using Doctheca.Database.Repositories;
using Doctheca.Domain.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
        await new DocthecaMigrationExecutor(context).ExecuteAsync();
        await MigrationGoldenStates.SeedBusinessRowsAsync(context);
        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO document_parses (id,document_file_id,model_version,status)
            VALUES ({_otherParse},{MigrationGoldenStates.FileId},'vlm','parsing')
            """);
    }
    public Task DisposeAsync() => fixture.DropDatabaseAsync(_database);

    [Fact]
    public async Task Replace_ReadBatch_IdempotencyAndEmptyReplacementRespectExactOwnership()
    {
        using var context = fixture.CreateContext(_database);
        var repository = new DocumentParsePageRepository(context);
        DocumentParsePageModel[] pages = [new(Parse, 1, "iv", "pdf-page-labels"), new(Parse, 2, "iv", "pdf-page-labels"),
            new(Parse, 3, "封面\n一", "page-number-blocks"), new(Parse, 4, null, null), new(Parse, 5, "", "")];
        await repository.ReplaceAsync(Parse, pages);
        await repository.ReplaceAsync(Parse, pages);
        await repository.ReplaceAsync(_otherParse, [new(_otherParse, 1, "foreign", null), new(_otherParse, 2, "other", null)]);
        var result = await repository.ReadBatchAsync([.. pages.Select(page => new DocumentParsePageKey(Parse, page.PageNumber)), new(Parse, 99)]);
        result.Values.OrderBy(page => page.PageNumber).Should().Equal(pages);
        // Different parse and page lists must not turn into a cross-product in the result.
        (await repository.ReadBatchAsync([new(Parse, 1), new(_otherParse, 2)])).Values.Should().HaveCount(2)
            .And.Contain(new DocumentParsePageModel(Parse, 1, "iv", "pdf-page-labels"))
            .And.Contain(new DocumentParsePageModel(_otherParse, 2, "other", null));
        await repository.ReplaceAsync(Parse, []);
        (await repository.ReadBatchAsync([new(Parse, 1)])).Should().BeEmpty();
        (await repository.ReadBatchAsync([new(_otherParse, 1)])).Should().ContainSingle();
        (await repository.ReadBatchAsync([new(Guid.NewGuid(), 1)])).Should().BeEmpty();
        (await repository.ReadBatchAsync([])).Should().BeEmpty();
    }

    [Fact]
    public async Task InvalidCompleteSet_LeavesOldRowsUntouched()
    {
        using var context = fixture.CreateContext(_database);
        var repository = new DocumentParsePageRepository(context);
        await repository.ReplaceAsync(Parse, [new(Parse, 1, "old", null)]);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ReplaceAsync(Parse, [new(Parse, 2, "new", null), new(Parse, 2, "duplicate", null)]));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ReplaceAsync(Parse, [new(Parse, 0, "invalid", null)]));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ReplaceAsync(Parse, [new(_otherParse, 1, "wrong owner", null)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ReplaceAsync(Guid.NewGuid(), []));
        (await repository.ReadBatchAsync([new(Parse, 1)])).Values.Single().Label.Should().Be("old");
    }

    [Fact]
    public async Task InsertFailure_RollsBackDeleteAndEarlierInserts()
    {
        using var context = fixture.CreateContext(_database);
        var repository = new DocumentParsePageRepository(context);
        await repository.ReplaceAsync(Parse, [new(Parse, 1, "old", null)]);
        await MigrationGoldenStates.ExecuteRawAsync(context, """
            CREATE FUNCTION reject_page() RETURNS trigger AS $$ BEGIN
              IF NEW.page_number = 2 THEN RAISE EXCEPTION 'synthetic insert failure'; END IF;
              RETURN NEW;
            END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER reject_page BEFORE INSERT ON document_parse_pages FOR EACH ROW EXECUTE FUNCTION reject_page();
            """);
        await Assert.ThrowsAnyAsync<Exception>(() => repository.ReplaceAsync(Parse, [new(Parse, 1, "new", null), new(Parse, 2, "new", null)]));
        (await repository.ReadBatchAsync([new(Parse, 1), new(Parse, 2)])).Values.Should().Equal(new DocumentParsePageModel(Parse, 1, "old", null));
        // Failure leaves no tracked pending insert to escape a later unit-of-work SaveChanges.
        await context.SaveChangesAsync();
        (await repository.ReadBatchAsync([new(Parse, 1)])).Values.Single().Label.Should().Be("old");
    }

    [Fact]
    public async Task ReaderSeesCommittedSet_AndConcurrentReplacementsSerializePerParse()
    {
        using var context = fixture.CreateContext(_database);
        await new DocumentParsePageRepository(context).ReplaceAsync(Parse, [new(Parse, 1, "old", null), new(Parse, 2, "old", null)]);
        var pause = new PauseAfterDelete();
        using var writer = Context(pause);
        using var writer2 = fixture.CreateContext(_database);
        var first = new DocumentParsePageRepository(writer).ReplaceAsync(Parse, [new(Parse, 1, "first", null)]);
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var old = await new DocumentParsePageRepository(context).ReadBatchAsync([new(Parse, 1), new(Parse, 2)]);
            old.Values.Select(page => page.Label).Should().Equal("old", "old");
            var second = new DocumentParsePageRepository(writer2).ReplaceAsync(Parse, [new(Parse, 1, "second", null), new(Parse, 3, "second", null)]);
            // A read in another parse is unaffected by the lock and cannot acquire foreign labels.
            (await new DocumentParsePageRepository(context).ReadBatchAsync([new(_otherParse, 1)])).Should().BeEmpty();
            second.IsCompleted.Should().BeFalse();
            pause.Release.TrySetResult();
            await Task.WhenAll(first, second);
            var final = await new DocumentParsePageRepository(context).ReadBatchAsync([new(Parse, 1), new(Parse, 2), new(Parse, 3)]);
            final.Values.OrderBy(page => page.PageNumber).Select(page => page.Label).Should().Equal("second", "second");
        }
        finally { pause.Release.TrySetResult(); await first; }
    }

    [Fact]
    public async Task CancellationAfterDelete_RollsBackWithoutPublishingPartialRows()
    {
        using var context = fixture.CreateContext(_database);
        await new DocumentParsePageRepository(context).ReplaceAsync(Parse, [new(Parse, 1, "old", null)]);
        using var cancellation = new CancellationTokenSource();
        var pause = new PauseAfterDelete();
        using var writer = Context(pause);
        var pending = new DocumentParsePageRepository(writer).ReplaceAsync(Parse, [], cancellation.Token);
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        (await new DocumentParsePageRepository(context).ReadBatchAsync([new(Parse, 1)])).Values.Single().Label.Should().Be("old");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParseAndDocumentDeletion_CascadePages(bool document)
    {
        using var context = fixture.CreateContext(_database);
        var repository = new DocumentParsePageRepository(context);
        await repository.ReplaceAsync(Parse, [new(Parse, 1, "label", null)]);
        if (document) await context.DocumentFiles.Where(file => file.Id == MigrationGoldenStates.FileId).ExecuteDeleteAsync();
        else await context.DocumentParses.Where(parse => parse.Id == Parse).ExecuteDeleteAsync();
        (await repository.ReadBatchAsync([new(Parse, 1)])).Should().BeEmpty();
    }

    private DocthecaDbContext Context(DbCommandInterceptor interceptor) => new(new DbContextOptionsBuilder<DocthecaDbContext>()
        .UseNpgsql(fixture.GetConnectionString(_database)).AddInterceptors(interceptor).Options);

    private sealed class PauseAfterDelete : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DELETE FROM", StringComparison.Ordinal) && command.CommandText.Contains("document_parse_pages", StringComparison.Ordinal))
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
