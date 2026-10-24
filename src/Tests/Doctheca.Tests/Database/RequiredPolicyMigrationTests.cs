using System.Data.Common;
using Doctheca.Database;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Doctheca.Tests.Database;

[Collection(MigrationIntegrationCollection.Name)]
public sealed class RequiredPolicyMigrationTests(PostgreSqlMigrationFixture fixture) : IAsyncLifetime
{
    private string _database = null!;
    private const string Previous = DocthecaMigrationExecutor.ClassifyLevelLessTitlesMigrationId;
    private const string Current = DocthecaMigrationExecutor.AddRequiredStructureLevelsMigrationId;
    public async Task InitializeAsync() => _database = await fixture.CreateDatabaseAsync();
    public Task DisposeAsync() => fixture.DropDatabaseAsync(_database);

    [Fact]
    public async Task PopulatedPreviousVersion_PreservesAllRowsAndLinks_AndAddsFalsePolicyOnly()
    {
        using var context = fixture.CreateContext(_database);
        await PreviousAsync(context);
        var rows = await RowsAsync(context);
        var columns = await MigrationGoldenStates.ReadLegacyColumnShapeAsync(context, DocthecaMigrationExecutor.AllTableNames);
        var constraints = await MigrationGoldenStates.ReadLegacyConstraintShapeAsync(context, DocthecaMigrationExecutor.AllTableNames);
        var executor = new DocthecaMigrationExecutor(context);
        (await executor.InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.PendingMigrations);
        await executor.ExecuteAsync();
        (await RowsAsync(context)).Should().Equal(rows);
        var after = await MigrationGoldenStates.ReadLegacyColumnShapeAsync(context, DocthecaMigrationExecutor.AllTableNames);
        after.Should().Contain("structure_levels.required:boolean:notnull");
        after.Where(column => !column.StartsWith("structure_levels.required:")).Should().Equal(columns);
        (await MigrationGoldenStates.ReadLegacyConstraintShapeAsync(context, DocthecaMigrationExecutor.AllTableNames)).Should().Equal(constraints);
        (await context.StructureLevels.AsNoTracking().ToListAsync()).Should().NotBeEmpty().And.OnlyContain(level => !level.Required);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(MigrationGoldenStates.AllIds);
        (await executor.InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.CurrentVersionCompatible);
    }

    [Fact]
    public async Task FreshMigrationAndEnsureCreated_HaveTheSameModelSchema()
    {
        var reference = await fixture.CreateDatabaseAsync();
        try
        {
            using var migrated = fixture.CreateContext(_database);
            using var model = fixture.CreateContext(reference);
            await new DocthecaMigrationExecutor(migrated).ExecuteAsync();
            await model.Database.EnsureCreatedAsync();
            (await MigrationGoldenStates.ReadColumnShapeAsync(migrated, DocthecaMigrationExecutor.AllTableNames)).Order().Should()
                .Equal((await MigrationGoldenStates.ReadColumnShapeAsync(model, DocthecaMigrationExecutor.AllTableNames)).Order());
            (await MigrationGoldenStates.ReadConstraintShapeAsync(migrated, DocthecaMigrationExecutor.AllTableNames)).Should()
                .Equal(await MigrationGoldenStates.ReadConstraintShapeAsync(model, DocthecaMigrationExecutor.AllTableNames));
            (await MigrationGoldenStates.ScalarAsync<string>(migrated,
                "SELECT column_default FROM information_schema.columns WHERE table_name='structure_levels' AND column_name='required'"))
                .Should().Be("false");
        }
        finally { await fixture.DropDatabaseAsync(reference); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Down_RestoresExactPreviousSchemaHistoryAndRows_AndForwardResetsTruePolicy(bool documentedSql)
    {
        using var context = fixture.CreateContext(_database);
        await PreviousAsync(context);
        var rows = await RowsAsync(context);
        var columns = await MigrationGoldenStates.ReadLegacyColumnShapeAsync(context, DocthecaMigrationExecutor.AllTableNames);
        var constraints = await MigrationGoldenStates.ReadLegacyConstraintShapeAsync(context, DocthecaMigrationExecutor.AllTableNames);
        var history = await MigrationGoldenStates.ReadAppliedHistoryAsync(context);
        var executor = new DocthecaMigrationExecutor(context);
        await executor.ExecuteAsync();
        await MigrationGoldenStates.ExecuteRawAsync(context, "UPDATE structure_levels SET required = true");
        (await context.StructureLevels.AsNoTracking().ToListAsync()).Should().OnlyContain(level => level.Required);
        if (documentedSql)
        {
            await MigrationGoldenStates.ExecuteRawAsync(context, await File.ReadAllTextAsync(StructureMigrationTests.RollbackScriptPath(DocthecaMigrationExecutor.AddDocumentOwnedStructureMigrationId)));
            await MigrationGoldenStates.ExecuteRawAsync(context, await File.ReadAllTextAsync(StructureMigrationTests.RollbackScriptPath(Current)));
        }
        else
            await context.GetService<IMigrator>().MigrateAsync(Previous);
        (await RowsAsync(context)).Should().Equal(rows);
        (await MigrationGoldenStates.ReadLegacyColumnShapeAsync(context, DocthecaMigrationExecutor.AllTableNames)).Should().Equal(columns);
        (await MigrationGoldenStates.ReadLegacyConstraintShapeAsync(context, DocthecaMigrationExecutor.AllTableNames)).Should().Equal(constraints);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(history);
        await executor.ExecuteAsync();
        (await RowsAsync(context)).Should().Equal(rows);
        (await context.StructureLevels.AsNoTracking().ToListAsync()).Should().OnlyContain(level => !level.Required);
    }

    [Theory]
    [InlineData("unrecorded")]
    [InlineData("missing")]
    [InlineData("wrong-type")]
    [InlineData("nullable")]
    [InlineData("unknown")]
    [InlineData("gapped")]
    public async Task ConflictingSchemaOrHistory_IsRefusedWithoutChangingSchemaHistoryOrRows(string scenario)
    {
        using var context = fixture.CreateContext(_database);
        await PreviousAsync(context);
        if (scenario == "unrecorded")
            await MigrationGoldenStates.ExecuteRawAsync(context, "ALTER TABLE structure_levels ADD COLUMN required boolean NOT NULL DEFAULT false");
        else
        {
            await new DocthecaMigrationExecutor(context).ExecuteAsync();
            await MigrationGoldenStates.ExecuteRawAsync(context, scenario switch
            {
                "missing" => "ALTER TABLE structure_levels DROP COLUMN required",
                "wrong-type" => "ALTER TABLE structure_levels ALTER COLUMN required DROP DEFAULT; ALTER TABLE structure_levels ALTER COLUMN required TYPE text USING required::text",
                "nullable" => "ALTER TABLE structure_levels ALTER COLUMN required DROP NOT NULL",
                // Unknown history must win even when the schema is also incompatible.
                "unknown" => "ALTER TABLE structure_levels DROP COLUMN required; INSERT INTO \"__EFMigrationsHistory\" VALUES ('29990101000000_Unknown', '10.0.13')",
                _ => "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"='20261022194526_ClassifyLevelLessTitles'"
            });
        }
        var history = await MigrationGoldenStates.ReadAppliedHistoryAsync(context);
        var columns = await MigrationGoldenStates.ReadLegacyColumnShapeAsync(context, DocthecaMigrationExecutor.AllTableNames);
        var rows = await RowsAsync(context);
        var executor = new DocthecaMigrationExecutor(context);
        (await executor.InspectAsync()).Should().Be(scenario == "unknown" ? DocthecaMigrationExecutor.DatabaseState.VersionTooNew
            : DocthecaMigrationExecutor.DatabaseState.InspectionFailed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync());
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(history);
        (await MigrationGoldenStates.ReadLegacyColumnShapeAsync(context, DocthecaMigrationExecutor.AllTableNames)).Should().Equal(columns);
        (await RowsAsync(context)).Should().Equal(rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationBeforeCommit_RollsBackColumnAndHistory_ThenRetries(bool cancel)
    {
        using var setup = fixture.CreateContext(_database);
        await PreviousAsync(setup);
        var rows = await RowsAsync(setup);
        var history = await MigrationGoldenStates.ReadAppliedHistoryAsync(setup);
        var columns = await MigrationGoldenStates.ReadLegacyColumnShapeAsync(setup, DocthecaMigrationExecutor.AllTableNames);
        using var cancellation = new CancellationTokenSource();
        using (var attempt = new DocthecaDbContext(new DbContextOptionsBuilder<DocthecaDbContext>()
                   .UseNpgsql(fixture.GetConnectionString(_database)).AddInterceptors(new BeforeCommit(cancellation, cancel)).Options))
        {
            var error = await Record.ExceptionAsync(() => new DocthecaMigrationExecutor(attempt).ExecuteAsync(cancellation.Token));
            error.Should().NotBeNull();
            if (cancel) error.Should().BeAssignableTo<OperationCanceledException>();
        }
        (await RowsAsync(setup)).Should().Equal(rows);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(setup)).Should().Equal(history);
        (await MigrationGoldenStates.ReadLegacyColumnShapeAsync(setup, DocthecaMigrationExecutor.AllTableNames)).Should().Equal(columns);
        await new DocthecaMigrationExecutor(setup).ExecuteAsync();
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(setup)).Should().Equal(MigrationGoldenStates.AllIds);
    }

    [Fact]
    public async Task RepeatedConcurrentStartup_AppliesPendingPolicyOnceUnderAdvisoryLock()
    {
        using var setup = fixture.CreateContext(_database);
        await PreviousAsync(setup);
        var rows = await RowsAsync(setup);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            using var context = fixture.CreateContext(_database);
            return await MigrationOrchestration.OrchestrateAsync(context, fixture.GetConnectionString(_database));
        }));
        results.Should().OnlyContain(result => result.Succeeded);
        results.Count(result => result.ExecutorWasCalled).Should().Be(1);
        (await MigrationOrchestration.OrchestrateAsync(setup, fixture.GetConnectionString(_database))).ExecutorWasCalled.Should().BeFalse();
        (await RowsAsync(setup)).Should().Equal(rows);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(setup)).Should().Equal(MigrationGoldenStates.AllIds);
    }

    private static async Task PreviousAsync(DocthecaDbContext context)
    {
        await context.GetService<IMigrator>().MigrateAsync(Previous);
        await PageMigrationTests.SeedAllEarlierTablesAsync(context);
        await context.Database.ExecuteSqlAsync($"INSERT INTO document_parse_pages VALUES ({MigrationGoldenStates.ParseId},1,{"one"},{"synthetic"})");
        await MigrationGoldenStates.ExecuteRawAsync(context, "UPDATE document_parse_blocks SET caption='caption', footnote='footnote'");
        // Populate non-null level/node parent links before the column exists.
        var child = Guid.NewGuid();
        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO structure_levels (id,definition_id,name,parent_level_id,sort_order)
            SELECT {child},definition_id,'Child',id,1 FROM structure_levels WHERE parent_level_id IS NULL;
            INSERT INTO document_structure_nodes (id,parse_id,level_id,parent_id,name,sort_order,start_sequence,end_sequence,created_at,updated_at)
            SELECT {Guid.NewGuid()},parse_id,{child},id,'Child node',0,0,0,created_at,updated_at
            FROM document_structure_nodes WHERE parent_id IS NULL;
            """);
    }

    private static async Task<List<string>> RowsAsync(DocthecaDbContext context)
    {
        var result = new List<string>();
        foreach (var table in DocthecaMigrationExecutor.PreOwnershipTableNames)
        {
            await context.Database.OpenConnectionAsync();
            try
            {
                await using var command = context.Database.GetDbConnection().CreateCommand();
                command.CommandText = $"SELECT '{table}:' || ({MigrationGoldenStates.LegacyRowJson(table)} - 'required')::text FROM {table} t ORDER BY 1";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) result.Add(reader.GetString(0));
            }
            finally { await context.Database.CloseConnectionAsync(); }
        }
        return result;
    }

    private sealed class BeforeCommit(CancellationTokenSource cancellation, bool cancel) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("ADD required boolean", StringComparison.Ordinal))
            {
                if (cancel) cancellation.Cancel();
                else throw new InvalidOperationException("Synthetic pre-commit failure");
            }
            return ValueTask.FromResult(result);
        }
    }
}
