using System.Data.Common;
using Doctheca.Database;
using Doctheca.Tests.Parsing;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Doctheca.Tests.Database;

[Collection(MigrationIntegrationCollection.Name)]
public sealed class HeadingClassificationMigrationTests(PostgreSqlMigrationFixture fixture) : IAsyncLifetime
{
    private string _database = null!;
    public async Task InitializeAsync() => _database = await fixture.CreateDatabaseAsync();
    public Task DisposeAsync() => fixture.DropDatabaseAsync(_database);

    [Fact]
    public async Task PopulatedAnnotationVersion_CorrectsOnlyTargetLevels_WithIdenticalSchemaAndEveryOtherValue()
    {
        using var context = fixture.CreateContext(_database);
        var rows = await EarlierVersionAsync(context);
        var before = await SnapshotAsync(context);
        var expected = await LevelsAsync(context);
        foreach (var row in rows) expected[row.Id] = row.After;
        var columns = await MigrationGoldenStates.ReadColumnShapeAsync(context, DocthecaMigrationExecutor.AllTableNames);
        var constraints = await MigrationGoldenStates.ReadConstraintShapeAsync(context, DocthecaMigrationExecutor.AllTableNames);
        var executor = new DocthecaMigrationExecutor(context);
        (await executor.InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.PendingMigrations);
        await executor.ExecuteAsync();
        (await LevelsAsync(context)).Should().BeEquivalentTo(expected);
        (await SnapshotAsync(context)).Should().Equal(before);
        (await MigrationGoldenStates.ReadColumnShapeAsync(context, DocthecaMigrationExecutor.AllTableNames)).Where(column => !column.StartsWith("structure_levels.required:")).Should().Equal(columns);
        (await MigrationGoldenStates.ReadConstraintShapeAsync(context, DocthecaMigrationExecutor.AllTableNames)).Should().Equal(constraints);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(MigrationGoldenStates.AllIds);
        (await executor.InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.CurrentVersionCompatible);
        // C# and SQL consume the same representable stored-input corpus, not separate examples.
        foreach (var row in rows.Where(row => row.FromCorpus && row.Type == "title"))
            expected[row.Id].Should().Be(row.RuntimeLevel > 0 ? -1 : -2);
    }

    [Fact]
    public async Task VerifiedLegacyTakeover_PreservesLegacyPositiveAndBodyZero_ThenAppliesCorrection()
    {
        using var context = fixture.CreateContext(_database);
        await MigrationGoldenStates.EnsureCreatedLegacyAsync(context);
        await MigrationGoldenStates.SeedBusinessRowsAsync(context);
        var rows = await InsertCasesAsync(context);
        var before = await SnapshotAsync(context, legacy: true);
        var expected = await LevelsAsync(context);
        foreach (var row in rows) expected[row.Id] = row.After;
        var executor = new DocthecaMigrationExecutor(context);
        (await executor.InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.LegacyTakeoverRequired);
        var result = await MigrationOrchestration.OrchestrateAsync(context, fixture.GetConnectionString(_database));
        result.Succeeded.Should().BeTrue();
        (await LevelsAsync(context)).Should().BeEquivalentTo(expected);
        (await SnapshotAsync(context, legacy: true)).Should().Equal(before);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(MigrationGoldenStates.AllIds);
        (await executor.InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.CurrentVersionCompatible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Down_CollapsesOnlyTitleMinusTwo_AndPreservesSchemaSiblingDataAndEarlierHistory(bool documentedSql)
    {
        using var context = fixture.CreateContext(_database);
        await EarlierVersionAsync(context);
        var executor = new DocthecaMigrationExecutor(context);
        await executor.ExecuteAsync();
        var before = await SnapshotAsync(context);
        var levels = await LevelsAsync(context);
        var titleIds = await context.DocumentParseBlocks.AsNoTracking().Where(row => row.BlockType == "title")
            .Select(row => row.Id).ToListAsync();
        var afterForward = new Dictionary<Guid, int>(levels);
        foreach (var id in titleIds.Where(id => levels[id] == -2)) levels[id] = -1;
        if (documentedSql)
        {
            await MigrationGoldenStates.ExecuteRawAsync(context, await File.ReadAllTextAsync(
                StructureMigrationTests.RollbackScriptPath(DocthecaMigrationExecutor.AddRequiredStructureLevelsMigrationId)));
            await MigrationGoldenStates.ExecuteRawAsync(context, await File.ReadAllTextAsync(
                StructureMigrationTests.RollbackScriptPath(DocthecaMigrationExecutor.ClassifyLevelLessTitlesMigrationId)));
        }
        else
            await context.GetService<IMigrator>().MigrateAsync(DocthecaMigrationExecutor.AddBlockFootnotesMigrationId);
        (await LevelsAsync(context)).Should().BeEquivalentTo(levels);
        (await SnapshotAsync(context)).Should().Equal(before);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(MigrationGoldenStates.AllIds.Take(5));
        (await executor.InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.PendingMigrations);
        await executor.ExecuteAsync();
        (await LevelsAsync(context)).Should().BeEquivalentTo(afterForward);
        (await SnapshotAsync(context)).Should().Equal(before);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(MigrationGoldenStates.AllIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationBeforeCommit_RollsBackCorrectionAndHistory_AndRetryIsSafe(bool cancel)
    {
        using var setup = fixture.CreateContext(_database);
        await EarlierVersionAsync(setup);
        var before = await SnapshotAsync(setup);
        var levels = await LevelsAsync(setup);
        var history = await MigrationGoldenStates.ReadAppliedHistoryAsync(setup);
        using var cancellation = new CancellationTokenSource();
        if (!cancel)
            await MigrationGoldenStates.ExecuteRawAsync(setup, """
                CREATE FUNCTION reject_heading_correction() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'synthetic heading correction failure'; END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_heading_correction BEFORE UPDATE ON document_parse_blocks
                FOR EACH ROW EXECUTE FUNCTION reject_heading_correction();
                """);
        var options = new DbContextOptionsBuilder<DocthecaDbContext>()
            .UseNpgsql(fixture.GetConnectionString(_database));
        if (cancel) options.AddInterceptors(new CancelAfterCorrection(cancellation));
        using (var attempt = new DocthecaDbContext(options.Options))
        {
            var error = await Record.ExceptionAsync(() => new DocthecaMigrationExecutor(attempt).ExecuteAsync(cancellation.Token));
            error.Should().NotBeNull();
            if (cancel) error.Should().BeAssignableTo<OperationCanceledException>();
        }
        (await SnapshotAsync(setup)).Should().Equal(before);
        (await LevelsAsync(setup)).Should().BeEquivalentTo(levels);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(setup)).Should().Equal(history);
        if (!cancel)
            await MigrationGoldenStates.ExecuteRawAsync(setup,
                "DROP TRIGGER reject_heading_correction ON document_parse_blocks; DROP FUNCTION reject_heading_correction();");
        var result = await MigrationOrchestration.OrchestrateAsync(setup, fixture.GetConnectionString(_database));
        result.Succeeded.Should().BeTrue();
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(setup)).Should().Equal(MigrationGoldenStates.AllIds);
    }

    [Fact]
    public async Task ConcurrentAndRepeatedStarts_ApplyDataMigrationOnceUnderAdvisoryLock()
    {
        using var setup = fixture.CreateContext(_database);
        await EarlierVersionAsync(setup);
        var before = await SnapshotAsync(setup);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            using var context = fixture.CreateContext(_database);
            return await MigrationOrchestration.OrchestrateAsync(context, fixture.GetConnectionString(_database));
        }));
        results.Should().OnlyContain(result => result.Succeeded);
        results.Count(result => result.ExecutorWasCalled).Should().Be(1);
        var corrected = await LevelsAsync(setup);
        (await MigrationOrchestration.OrchestrateAsync(setup, fixture.GetConnectionString(_database)))
            .ExecutorWasCalled.Should().BeFalse();
        (await LevelsAsync(setup)).Should().BeEquivalentTo(corrected);
        (await SnapshotAsync(setup)).Should().Equal(before);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(setup)).Should().Equal(MigrationGoldenStates.AllIds);
    }

    private static async Task<List<Row>> EarlierVersionAsync(DocthecaDbContext context)
    {
        await context.GetService<IMigrator>().MigrateAsync(DocthecaMigrationExecutor.AddBlockFootnotesMigrationId);
        await PageMigrationTests.SeedAllEarlierTablesAsync(context);
        await context.Database.ExecuteSqlAsync($"""
            UPDATE document_parse_blocks SET caption={"caption α\n"}, footnote={"footnote β\r\n"};
            INSERT INTO document_parse_pages VALUES ({MigrationGoldenStates.ParseId}, 1, {"页一"}, {"synthetic"});
            """);
        return await InsertCasesAsync(context);
    }

    private static async Task<List<Row>> InsertCasesAsync(DocthecaDbContext context)
    {
        var rows = HeadingLevelCases.All.Where(value => value[0] is string && (value[1] as string)?.Length is not > 50)
            .Select(value => new Row(Guid.NewGuid(), (string)value[0]!, (string?)value[1], -1,
                (string)value[0]! == "title" && (int)value[2]! <= 0 ? -2 : -1, true, (int)value[2]!)).ToList();
        rows.AddRange([
            new(Guid.NewGuid(), "title", null, 7, 7),
            new(Guid.NewGuid(), "title", "malformed", 9, 9),
            new(Guid.NewGuid(), "title", "heading-" + new string('0', 42), 2, 2),
            new(Guid.NewGuid(), "title", "heading-+2", 0, 0),
            new(Guid.NewGuid(), "title", "bad", -9, -2),
            new(Guid.NewGuid(), "title", null, -2, -2),
            new(Guid.NewGuid(), "text", null, 0, 0),
            new(Guid.NewGuid(), "text", "bad", 8, 8),
            new(Guid.NewGuid(), "image", null, -2, -2)
        ]);
        var sort = 100;
        foreach (var row in rows)
            await context.Database.ExecuteSqlAsync($"""
                INSERT INTO document_parse_blocks (id,parse_id,page_id,sort_index,block_type,text_content,
                    image_id,block_data,created_at,sub_type,text_level,text_format,bbox_x0,bbox_y0,bbox_x1,bbox_y1,score,caption)
                VALUES ({row.Id},{MigrationGoldenStates.ParseId},3,{sort++},{row.Type},{"synthetic heading text"},
                    {MigrationGoldenStates.ImageId},{"{\"original\":true}"}::jsonb,'2026-01-01T00:00:00Z',
                    {row.Subtype},{row.Before},'plain',1,2,3,4,0.75,{"raw caption"});
                """);
        return rows;
    }

    private static Task<Dictionary<Guid, int>> LevelsAsync(DocthecaDbContext context) =>
        context.DocumentParseBlocks.AsNoTracking().Select(row => new { row.Id, row.TextLevel })
            .ToDictionaryAsync(row => row.Id, row => row.TextLevel);

    private static async Task<List<string>> SnapshotAsync(DocthecaDbContext context, bool legacy = false)
    {
        var rows = new List<string>();
        foreach (var table in legacy ? DocthecaMigrationExecutor.KnownTableNames : DocthecaMigrationExecutor.AllTableNames)
        {
            await context.Database.OpenConnectionAsync();
            try
            {
                await using var command = context.Database.GetDbConnection().CreateCommand();
                var removed = legacy ? " - 'sequence' - 'structure_definition_id' - 'footnote'" : "";
                command.CommandText = $"SELECT '{table}:' || (to_jsonb(t) - 'text_level' - 'required'{removed})::text FROM {table} t ORDER BY 1";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
            }
            finally { await context.Database.CloseConnectionAsync(); }
        }
        return rows;
    }

    private sealed record Row(Guid Id, string Type, string? Subtype, int Before, int After,
        bool FromCorpus = false, int RuntimeLevel = -1);

    private sealed class CancelAfterCorrection(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SET text_level = -2", StringComparison.Ordinal)) cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }
}
