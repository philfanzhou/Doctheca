using Doctheca.Database;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Doctheca.Tests.Database;

[Collection(MigrationIntegrationCollection.Name)]
public sealed class AnnotationMigrationTests(PostgreSqlMigrationFixture fixture) : IAsyncLifetime
{
    private string _database = null!;
    public async Task InitializeAsync() => _database = await fixture.CreateDatabaseAsync();
    public Task DisposeAsync() => fixture.DropDatabaseAsync(_database);

    [Fact]
    public async Task PopulatedPageVersion_UpgradeAddsOnlyNullableFootnoteAndKeepsEveryExistingValue()
    {
        using var context = fixture.CreateContext(_database);
        await context.GetService<IMigrator>().MigrateAsync(DocthecaMigrationExecutor.AddDocumentParsePagesMigrationId);
        await SeedAsync(context);
        var before = await ExistingRowsAsync(context);
        var columns = await MigrationGoldenStates.ReadColumnShapeAsync(context, DocthecaMigrationExecutor.AllTableNames);
        var constraints = await MigrationGoldenStates.ReadConstraintShapeAsync(context, DocthecaMigrationExecutor.AllTableNames);
        var executor = new DocthecaMigrationExecutor(context);
        (await executor.InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.PendingMigrations);
        await executor.ExecuteAsync();
        (await executor.InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.CurrentVersionCompatible);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(MigrationGoldenStates.AllIds);
        (await ExistingRowsAsync(context)).Should().Equal(before);
        var afterColumns = await MigrationGoldenStates.ReadColumnShapeAsync(context, DocthecaMigrationExecutor.AllTableNames);
        afterColumns.Should().Contain("document_parse_blocks.footnote:text:null");
        afterColumns.Where(column => !column.StartsWith("document_parse_blocks.footnote:")).Should().Equal(columns);
        (await MigrationGoldenStates.ReadConstraintShapeAsync(context, DocthecaMigrationExecutor.AllTableNames)).Should().Equal(constraints);
        (await context.DocumentParseBlocks.SingleAsync()).Footnote.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Down_RestoresExactPageVersionAndOnlyDiscardsFootnotes(bool documentedSql)
    {
        var reference = await fixture.CreateDatabaseAsync();
        try
        {
            using var previous = fixture.CreateContext(reference);
            await previous.GetService<IMigrator>().MigrateAsync(DocthecaMigrationExecutor.AddDocumentParsePagesMigrationId);
            using var context = fixture.CreateContext(_database);
            await new DocthecaMigrationExecutor(context).ExecuteAsync();
            await SeedAsync(context);
            await context.Database.ExecuteSqlAsync($"UPDATE document_parse_blocks SET footnote = {"新注释\nα"}");
            var before = await ExistingRowsAsync(context);
            if (documentedSql)
                await MigrationGoldenStates.ExecuteRawAsync(context, await File.ReadAllTextAsync(
                    StructureMigrationTests.RollbackScriptPath(DocthecaMigrationExecutor.AddBlockFootnotesMigrationId)));
            else
                await context.GetService<IMigrator>().MigrateAsync(DocthecaMigrationExecutor.AddDocumentParsePagesMigrationId);
            (await ExistingRowsAsync(context)).Should().Equal(before);
            (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(MigrationGoldenStates.AllIds.Take(4));
            (await MigrationGoldenStates.ReadColumnShapeAsync(context, DocthecaMigrationExecutor.AllTableNames)).Should()
                .Equal(await MigrationGoldenStates.ReadColumnShapeAsync(previous, DocthecaMigrationExecutor.AllTableNames));
            (await MigrationGoldenStates.ReadConstraintShapeAsync(context, DocthecaMigrationExecutor.AllTableNames)).Should()
                .Equal(await MigrationGoldenStates.ReadConstraintShapeAsync(previous, DocthecaMigrationExecutor.AllTableNames));
            var executor = new DocthecaMigrationExecutor(context);
            (await executor.InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.PendingMigrations);
            await executor.ExecuteAsync();
            (await executor.InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.CurrentVersionCompatible);
            (await ExistingRowsAsync(context)).Should().Equal(before);
            (await context.DocumentParseBlocks.SingleAsync()).Footnote.Should().BeNull();
        }
        finally { await fixture.DropDatabaseAsync(reference); }
    }

    [Theory]
    [InlineData("unrecorded")]
    [InlineData("missing")]
    [InlineData("wrong-type")]
    [InlineData("unknown-history")]
    public async Task ConflictingSchemaOrHistory_IsRefusedWithoutWrites(string scenario)
    {
        using var context = fixture.CreateContext(_database);
        if (scenario == "unrecorded")
        {
            await context.GetService<IMigrator>().MigrateAsync(DocthecaMigrationExecutor.AddDocumentParsePagesMigrationId);
            await MigrationGoldenStates.ExecuteRawAsync(context, "ALTER TABLE document_parse_blocks ADD COLUMN footnote text");
        }
        else
        {
            await new DocthecaMigrationExecutor(context).ExecuteAsync();
            await MigrationGoldenStates.ExecuteRawAsync(context, scenario switch
            {
                "missing" => "ALTER TABLE document_parse_blocks DROP COLUMN footnote",
                "wrong-type" => "ALTER TABLE document_parse_blocks ALTER COLUMN footnote TYPE varchar(10)",
                _ => "INSERT INTO \"__EFMigrationsHistory\" VALUES ('29990101000000_Unknown', '10.0.13')"
            });
        }
        var history = await MigrationGoldenStates.ReadAppliedHistoryAsync(context);
        var columns = await MigrationGoldenStates.ReadColumnShapeAsync(context, DocthecaMigrationExecutor.AllTableNames);
        var executor = new DocthecaMigrationExecutor(context);
        (await executor.InspectAsync()).Should().Be(scenario == "unknown-history"
            ? DocthecaMigrationExecutor.DatabaseState.VersionTooNew : DocthecaMigrationExecutor.DatabaseState.InspectionFailed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync());
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(history);
        (await MigrationGoldenStates.ReadColumnShapeAsync(context, DocthecaMigrationExecutor.AllTableNames)).Should().Equal(columns);
    }

    private static async Task SeedAsync(DocthecaDbContext context)
    {
        await PageMigrationTests.SeedAllEarlierTablesAsync(context);
        await context.Database.ExecuteSqlAsync($"UPDATE document_parse_blocks SET caption = {"原图题\nβ"}");
        await context.Database.ExecuteSqlAsync($"INSERT INTO document_parse_pages VALUES ({MigrationGoldenStates.ParseId}, 1, {"页一"}, {"synthetic"})");
    }

    private static async Task<List<string>> ExistingRowsAsync(DocthecaDbContext context)
    {
        var rows = new List<string>();
        foreach (var table in DocthecaMigrationExecutor.AllTableNames)
        {
            await context.Database.OpenConnectionAsync();
            try
            {
                await using var command = context.Database.GetDbConnection().CreateCommand();
                command.CommandText = $"SELECT '{table}:' || (to_jsonb(t) - 'footnote')::text FROM {table} t ORDER BY 1";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
            }
            finally { await context.Database.CloseConnectionAsync(); }
        }
        return rows;
    }
}
