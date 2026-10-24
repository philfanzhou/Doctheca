using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Doctheca.Database;
using Xunit;

namespace Doctheca.Tests.Database;

/// <summary>
/// The third known migration (issue #90) against real PostgreSQL: a populated database at the
/// structure version is classified as pending and migrated in place without changing any row; a
/// structure-version history with the audio table already present is refused; the documented
/// optional destructive down script returns the database to exactly the structure version the previous image verifies.
/// </summary>
[Collection(MigrationIntegrationCollection.Name)]
public sealed class AudioMigrationTests(PostgreSqlMigrationFixture fixture) : IAsyncLifetime
{
    private const string StructureId = DocthecaMigrationExecutor.AddDocumentStructureMigrationId;
    private const string AudioId = DocthecaMigrationExecutor.AddStructureAudioMigrationId;

    private string _database = null!;

    public async Task InitializeAsync() => _database = await fixture.CreateDatabaseAsync();

    public Task DisposeAsync() => fixture.DropDatabaseAsync(_database);

    private static DocthecaMigrationExecutor Executor(DocthecaDbContext context) => new(context, NullLogger.Instance);

    [Fact]
    public async Task PopulatedStructureVersion_IsMigratedInPlace_KeepingEveryRow()
    {
        using var context = fixture.CreateContext(_database);
        await context.GetService<IMigrator>().MigrateAsync(StructureId);
        await MigrationGoldenStates.SeedBusinessRowsAsync(context);
        await SeedStructureAsync(context);
        var before = await SnapshotAsync(context);

        var inspection = await Executor(context).InspectDetailedAsync(default);
        inspection.State.Should().Be(DocthecaMigrationExecutor.DatabaseState.PendingMigrations);

        await Executor(context).ExecuteAsync();

        (await Executor(context).InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.CurrentVersionCompatible);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(MigrationGoldenStates.AllIds);
        (await SnapshotAsync(context)).Should().Equal(before);
        (await MigrationGoldenStates.CountRowsAsync(context, "document_structure_audio")).Should().Be(0);
    }

    [Fact]
    public async Task DestructiveDownScript_RestoresTheExactStructureVersionAndKeepsEveryOtherRow()
    {
        var reference = await fixture.CreateDatabaseAsync();
        try
        {
            using var referenceContext = fixture.CreateContext(reference);
            await referenceContext.GetService<IMigrator>().MigrateAsync(StructureId);

            using var context = fixture.CreateContext(_database);
            await Executor(context).ExecuteAsync();
            await MigrationGoldenStates.SeedBusinessRowsAsync(context);
            await SeedStructureAsync(context);
            var before = await SnapshotAsync(context);
            await MigrationGoldenStates.ExecuteRawAsync(context, $"""
                INSERT INTO document_structure_audio (document_file_id, tree_id, id, parse_id, node_id, source_path, content_type, size_bytes,
                                                      object_key, match_status, link_source, created_at, updated_at)
                SELECT document_file_id,id,'{Guid.NewGuid()}','{MigrationGoldenStates.ParseId}',NULL,'unit1/a.mp3','audio/mpeg',10,
                       'audio/x/y.mp3','no_match',NULL,NOW(),NOW() FROM document_structure_trees WHERE origin_parse_id='{MigrationGoldenStates.ParseId}';
                """);

            await MigrationGoldenStates.ExecuteRawAsync(context, await File.ReadAllTextAsync(
                StructureMigrationTests.RollbackScriptPath(DocthecaMigrationExecutor.AddDocumentOwnedStructureMigrationId)));
            await MigrationGoldenStates.ExecuteRawAsync(context, await File.ReadAllTextAsync(
                StructureMigrationTests.RollbackScriptPath(DocthecaMigrationExecutor.AddRequiredStructureLevelsMigrationId)));
            await MigrationGoldenStates.ExecuteRawAsync(context, await File.ReadAllTextAsync(
                StructureMigrationTests.RollbackScriptPath(DocthecaMigrationExecutor.ClassifyLevelLessTitlesMigrationId)));
            await MigrationGoldenStates.ExecuteRawAsync(context, await File.ReadAllTextAsync(
                StructureMigrationTests.RollbackScriptPath(DocthecaMigrationExecutor.AddBlockFootnotesMigrationId)));
            await MigrationGoldenStates.ExecuteRawAsync(context, await File.ReadAllTextAsync(
                StructureMigrationTests.RollbackScriptPath(DocthecaMigrationExecutor.AddDocumentParsePagesMigrationId)));
            await MigrationGoldenStates.ExecuteRawAsync(context, await File.ReadAllTextAsync(StructureMigrationTests.RollbackScriptPath(AudioId)));

            (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(MigrationGoldenStates.InitialId, StructureId);
            var tables = DocthecaMigrationExecutor.StructureVersionTableNames;
            (await MigrationGoldenStates.ReadLegacyColumnShapeAsync(context, tables)).Should().Equal(await MigrationGoldenStates.ReadLegacyColumnShapeAsync(referenceContext, tables));
            (await MigrationGoldenStates.ReadLegacyConstraintShapeAsync(context, tables)).Should().Equal(await MigrationGoldenStates.ReadLegacyConstraintShapeAsync(referenceContext, tables));
            (await MigrationGoldenStates.ScalarAsync<bool>(context, "SELECT to_regclass('public.document_structure_audio') IS NULL")).Should().BeTrue();
            (await SnapshotAsync(context)).Should().Equal(before);
            // The previous version verifies exactly; this application migrates it forward again.
            (await Executor(context).InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.PendingMigrations);
            await Executor(context).ExecuteAsync();
            (await Executor(context).InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.CurrentVersionCompatible);
        }
        finally
        {
            await fixture.DropDatabaseAsync(reference);
        }
    }

    [Fact]
    public async Task StructureHistory_WithTheAudioTableAlreadyPresent_IsRefused()
    {
        using var context = fixture.CreateContext(_database);
        await context.GetService<IMigrator>().MigrateAsync(StructureId);
        await MigrationGoldenStates.ExecuteRawAsync(context, "CREATE TABLE document_structure_audio (id uuid PRIMARY KEY)");

        var inspection = await Executor(context).InspectDetailedAsync(default);
        inspection.State.Should().Be(DocthecaMigrationExecutor.DatabaseState.InspectionFailed);
        inspection.Reason.Should().Contain("document_structure_audio");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Executor(context).ExecuteAsync());
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(MigrationGoldenStates.InitialId, StructureId);
    }

    [Fact]
    public async Task CurrentHistory_WithAWrongAudioTable_IsRefused()
    {
        using var context = fixture.CreateContext(_database);
        await Executor(context).ExecuteAsync();
        await MigrationGoldenStates.ExecuteRawAsync(context, "ALTER TABLE document_structure_audio ADD COLUMN extra text");

        (await Executor(context).InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.InspectionFailed);
    }

    private static async Task SeedStructureAsync(DocthecaDbContext context)
    {
        var definition = Guid.NewGuid();
        var level = Guid.NewGuid();
        await MigrationGoldenStates.ExecuteRawAsync(context, $"""
            INSERT INTO structure_definitions (id, name, created_at, updated_at) VALUES ('{definition}', 'D', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            INSERT INTO structure_levels (id, definition_id, name, parent_level_id, sort_order) VALUES ('{level}', '{definition}', 'Unit', NULL, 0);
            UPDATE document_files SET structure_definition_id = '{definition}' WHERE id = '{MigrationGoldenStates.FileId}';
            UPDATE document_parse_blocks SET sequence = 0 WHERE id = '{MigrationGoldenStates.BlockId}';
            """);
        if (await MigrationGoldenStates.ScalarAsync<bool>(context, "SELECT to_regclass('public.document_structure_trees') IS NOT NULL"))
        {
            context.DocumentStructureNodes.Add(new Doctheca.Database.Entities.DocumentStructureNodeEntity { ParseId=MigrationGoldenStates.ParseId, LevelId=level, Name="Unit 1", StartSequence=0, EndSequence=0,
                CreatedAt=DateTimeOffset.Parse("2026-01-01T00:00:00Z"), UpdatedAt=DateTimeOffset.Parse("2026-01-01T00:00:00Z") });
            await context.SaveOwnedChangesAsync();
        }
        else await MigrationGoldenStates.ExecuteRawAsync(context, $"""
            INSERT INTO document_structure_nodes (id,parse_id,level_id,name,sort_order,start_sequence,end_sequence,created_at,updated_at)
            VALUES ('{Guid.NewGuid()}','{MigrationGoldenStates.ParseId}','{level}','Unit 1',0,0,0,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z');
            """);
    }

    /// <summary>Every row of every table of the structure version, as JSON text.</summary>
    private static async Task<List<string>> SnapshotAsync(DocthecaDbContext context)
    {
        var rows = new List<string>();
        foreach (var table in DocthecaMigrationExecutor.StructureVersionTableNames)
        {
            var connection = context.Database.GetDbConnection();
            await connection.OpenAsync();
            try
            {
                await using var command = connection.CreateCommand();
                // Compare the historical row fields across the later additive nullable column.
                command.CommandText = $"SELECT '{table}:' || ({MigrationGoldenStates.LegacyRowJson(table)} - 'footnote' - 'required')::text FROM {table} t ORDER BY 1";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
            }
            finally
            {
                await connection.CloseAsync();
            }
        }
        return rows;
    }
}
