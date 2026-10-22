using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Doctheca.Database;
using Xunit;

namespace Doctheca.Tests.Database;

/// <summary>
/// The second known migration (issue #89) against real PostgreSQL: a populated baseline database
/// is classified as pending, migrated in place with every existing block numbered and all other
/// data unchanged; inconsistent histories are refused; the documented rollback script returns the
/// database to the exact baseline the previous image verifies.
/// </summary>
[Collection(MigrationIntegrationCollection.Name)]
public sealed class StructureMigrationTests(PostgreSqlMigrationFixture fixture) : IAsyncLifetime
{
    private static readonly Guid StructaDocParse = Guid.Parse("e5555555-5555-4555-8555-555555555555");
    private static readonly Guid LegacyParse = Guid.Parse("f6666666-6666-4666-8666-666666666666");
    private static readonly Guid DuplicateParse = Guid.Parse("a7777777-7777-4777-8777-777777777777");

    private string _database = null!;

    public async Task InitializeAsync() => _database = await fixture.CreateDatabaseAsync();

    public Task DisposeAsync() => fixture.DropDatabaseAsync(_database);

    private static DocthecaMigrationExecutor Executor(DocthecaDbContext context) => new(context, NullLogger.Instance);

    [Fact]
    public async Task PopulatedBaseline_IsMigratedInPlace_BackfillingSequenceAndKeepingRows()
    {
        using var context = fixture.CreateContext(_database);
        await context.GetService<IMigrator>().MigrateAsync(MigrationGoldenStates.InitialId);
        await MigrationGoldenStates.SeedBusinessRowsAsync(context);
        await SeedParsesAsync(context);
        var blocksBefore = await RowsAsync(context, "SELECT to_jsonb(b)::text FROM document_parse_blocks b ORDER BY id");
        var filesBefore = await RowsAsync(context, "SELECT to_jsonb(f)::text FROM document_files f ORDER BY id");
        var parsesBefore = await RowsAsync(context, "SELECT to_jsonb(p)::text FROM document_parses p ORDER BY id");

        var inspection = await Executor(context).InspectDetailedAsync(default);
        inspection.State.Should().Be(DocthecaMigrationExecutor.DatabaseState.PendingMigrations);
        DocthecaMigrationExecutor.ToObservationState(inspection.State).Should()
            .Be(ServiceMantle.Migration.MigrationObservationState.PendingMigration);

        await Executor(context).ExecuteAsync();

        (await Executor(context).InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.CurrentVersionCompatible);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(MigrationGoldenStates.AllIds);

        // Every existing row is unchanged apart from the new nullable columns.
        (await RowsAsync(context, "SELECT (to_jsonb(b) - 'sequence' - 'footnote')::text FROM document_parse_blocks b ORDER BY id")).Should().Equal(blocksBefore);
        (await RowsAsync(context, "SELECT (to_jsonb(f) - 'structure_definition_id')::text FROM document_files f ORDER BY id")).Should().Equal(filesBefore);
        (await RowsAsync(context, "SELECT to_jsonb(p)::text FROM document_parses p ORDER BY id")).Should().Equal(parsesBefore);
        (await MigrationGoldenStates.ScalarAsync<long>(context, "SELECT count(*) FROM document_files WHERE structure_definition_id IS NOT NULL")).Should().Be(0);

        // Every block is numbered.
        (await MigrationGoldenStates.ScalarAsync<long>(context, "SELECT count(*) FROM document_parse_blocks WHERE sequence IS NULL")).Should().Be(0);
        // StructaDoc rows keep their own global sequence (deliberately not page order here).
        (await RowsAsync(context, $"SELECT sequence || ':' || (block_data ->> 'sequence') FROM document_parse_blocks WHERE parse_id = '{StructaDocParse}' ORDER BY sequence"))
            .Should().Equal("0:0", "1:1", "2:2", "3:3");
        (await RowsAsync(context, $"SELECT text_content FROM document_parse_blocks WHERE parse_id = '{StructaDocParse}' ORDER BY sequence"))
            .Should().Equal("s0", "s1", "s2", "s3");
        // Legacy rows without a sequence are numbered by (page_id, sort_index).
        (await RowsAsync(context, $"SELECT sequence || ':' || text_content FROM document_parse_blocks WHERE parse_id = '{LegacyParse}' ORDER BY sequence"))
            .Should().Equal("0:p0-0", "1:p0-1", "2:p1-0", "3:p2-0");
        // Duplicated StructaDoc values are not trusted; the whole parse is renumbered by page order.
        (await RowsAsync(context, $"SELECT sequence || ':' || text_content FROM document_parse_blocks WHERE parse_id = '{DuplicateParse}' ORDER BY sequence"))
            .Should().Equal("0:d0", "1:d1", "2:d2");
        // The baseline seed block (block_data '{}') is the only block of its parse.
        (await MigrationGoldenStates.ScalarAsync<int>(context, $"SELECT sequence FROM document_parse_blocks WHERE id = '{MigrationGoldenStates.BlockId}'")).Should().Be(0);
    }

    [Fact]
    public async Task RollbackScript_RestoresTheExactBaselineAndKeepsBusinessRows()
    {
        var baseline = await fixture.CreateDatabaseAsync();
        try
        {
            using var reference = fixture.CreateContext(baseline);
            await reference.GetService<IMigrator>().MigrateAsync(MigrationGoldenStates.InitialId);

            using var context = fixture.CreateContext(_database);
            await Executor(context).ExecuteAsync();
            await MigrationGoldenStates.SeedBusinessRowsAsync(context);
            await MigrationGoldenStates.ExecuteRawAsync(context, $"""
                INSERT INTO structure_definitions (id, name, created_at, updated_at) VALUES ('{Guid.NewGuid()}', 'D', NOW(), NOW());
                """);

            // Apply the documented down scripts newest first.
            await MigrationGoldenStates.ExecuteRawAsync(context, await File.ReadAllTextAsync(
                RollbackScriptPath(DocthecaMigrationExecutor.AddBlockFootnotesMigrationId)));
            await MigrationGoldenStates.ExecuteRawAsync(context, await File.ReadAllTextAsync(
                RollbackScriptPath(DocthecaMigrationExecutor.AddDocumentParsePagesMigrationId)));
            await MigrationGoldenStates.ExecuteRawAsync(context, await File.ReadAllTextAsync(
                RollbackScriptPath(DocthecaMigrationExecutor.AddStructureAudioMigrationId)));
            await MigrationGoldenStates.ExecuteRawAsync(context, await File.ReadAllTextAsync(
                RollbackScriptPath(DocthecaMigrationExecutor.AddDocumentStructureMigrationId)));

            (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(MigrationGoldenStates.InitialId);
            (await MigrationGoldenStates.ReadColumnShapeAsync(context)).Should().Equal(await MigrationGoldenStates.ReadColumnShapeAsync(reference));
            (await MigrationGoldenStates.ReadConstraintShapeAsync(context)).Should().Equal(await MigrationGoldenStates.ReadConstraintShapeAsync(reference));
            (await MigrationGoldenStates.ScalarAsync<bool>(context, "SELECT to_regclass('public.structure_definitions') IS NULL")).Should().BeTrue();
            (await MigrationGoldenStates.CountRowsAsync(context, "document_parse_blocks")).Should().Be(1);
            // The baseline verifies exactly (the previous image's own current-version check);
            // this application would simply migrate it forward again.
            (await Executor(context).InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.PendingMigrations);
            await Executor(context).ExecuteAsync();
            (await Executor(context).InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.CurrentVersionCompatible);
        }
        finally
        {
            await fixture.DropDatabaseAsync(baseline);
        }
    }

    [Fact]
    public async Task BaselineHistory_WithStructureTablesAlreadyPresent_IsRefused()
    {
        using var context = fixture.CreateContext(_database);
        await context.GetService<IMigrator>().MigrateAsync(MigrationGoldenStates.InitialId);
        await MigrationGoldenStates.ExecuteRawAsync(context, "CREATE TABLE structure_definitions (id uuid PRIMARY KEY)");

        var inspection = await Executor(context).InspectDetailedAsync(default);
        inspection.State.Should().Be(DocthecaMigrationExecutor.DatabaseState.InspectionFailed);
        inspection.Reason.Should().Contain("structure_definitions");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Executor(context).ExecuteAsync());
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(MigrationGoldenStates.InitialId);
    }

    [Fact]
    public async Task HistoryWithAGap_IsRefused()
    {
        using var context = fixture.CreateContext(_database);
        await Executor(context).ExecuteAsync();
        await MigrationGoldenStates.ExecuteRawAsync(context,
            $"DELETE FROM \"{MigrationGoldenStates.HistoryTable}\" WHERE \"MigrationId\" = '{MigrationGoldenStates.InitialId}'");

        var inspection = await Executor(context).InspectDetailedAsync(default);
        inspection.State.Should().Be(DocthecaMigrationExecutor.DatabaseState.InspectionFailed);
        inspection.Reason.Should().Contain("prefix");
    }

    [Fact]
    public async Task LegacyTablesWithoutHistory_PlusStructureTables_AreRefused()
    {
        using var context = fixture.CreateContext(_database);
        await MigrationGoldenStates.EnsureCreatedLegacyAsync(context);
        await MigrationGoldenStates.ExecuteRawAsync(context, "CREATE TABLE structure_levels (id uuid PRIMARY KEY)");

        (await Executor(context).InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.InspectionFailed);
    }

    internal static string RollbackScriptPath(string migrationId)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "docs", "database"))) root = root.Parent;
        return Path.Combine(root!.FullName, "docs", "database", "rollback", migrationId + ".down.sql");
    }

    private static async Task SeedParsesAsync(DocthecaDbContext context)
    {
        var file = MigrationGoldenStates.FileId;
        await MigrationGoldenStates.ExecuteRawAsync(context, $"""
            INSERT INTO document_parses (id, document_file_id, model_version, status) VALUES
                ('{StructaDocParse}', '{file}', 'vlm', 'parsed'),
                ('{LegacyParse}', '{file}', 'vlm', 'parsed'),
                ('{DuplicateParse}', '{file}', 'vlm', 'parsed');
            """);
        // (parse, page, sort, text, block_data); StructaDoc sequences intentionally disagree with page order.
        var rows = new (Guid Parse, int Page, int Sort, string Text, string Data)[]
        {
            (StructaDocParse, 1, 0, "s2", "{\"sequence\": 2}"),
            (StructaDocParse, 0, 0, "s0", "{\"sequence\": 0, \"type\": \"paragraph\"}"),
            (StructaDocParse, 0, 1, "s3", "{\"sequence\": 3}"),
            (StructaDocParse, 0, 2, "s1", "{\"sequence\": 1}"),
            (LegacyParse, 2, 0, "p2-0", "{\"type\": \"text\"}"),
            (LegacyParse, 0, 1, "p0-1", "{}"),
            (LegacyParse, 1, 0, "p1-0", "[1, 2]"),
            (LegacyParse, 0, 0, "p0-0", "{\"sequence\": \"x\"}"),
            (DuplicateParse, 0, 0, "d0", "{\"sequence\": 5}"),
            (DuplicateParse, 0, 1, "d1", "{\"sequence\": 5}"),
            (DuplicateParse, 1, 0, "d2", "{\"sequence\": 1}"),
        };
        foreach (var row in rows)
        {
            await context.Database.ExecuteSqlAsync($"""
                INSERT INTO document_parse_blocks (id, parse_id, page_id, sort_index, block_type, text_content,
                                                   block_data, created_at, text_level, text_format)
                VALUES ({Guid.NewGuid()}, {row.Parse}, {row.Page}, {row.Sort}, 'text', {row.Text},
                        {row.Data}::jsonb, '2026-01-02T00:00:00Z', -1, '')
                """);
        }
    }

    private static async Task<List<string>> RowsAsync(DocthecaDbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var rows = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
            return rows;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
