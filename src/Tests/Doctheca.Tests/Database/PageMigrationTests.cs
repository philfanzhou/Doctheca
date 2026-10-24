using Doctheca.Database;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Doctheca.Tests.Database;

[Collection(MigrationIntegrationCollection.Name)]
public sealed class PageMigrationTests(PostgreSqlMigrationFixture fixture) : IAsyncLifetime
{
    private string _database = null!;
    public async Task InitializeAsync() => _database = await fixture.CreateDatabaseAsync();
    public Task DisposeAsync() => fixture.DropDatabaseAsync(_database);

    [Fact]
    public async Task PopulatedAudioVersion_UpgradesWithoutChangingAnyExistingRow()
    {
        using var context = fixture.CreateContext(_database);
        await context.GetService<IMigrator>().MigrateAsync(DocthecaMigrationExecutor.AddStructureAudioMigrationId);
        await SeedAllEarlierTablesAsync(context);
        var before = await SnapshotAsync(context);
        var executor = new DocthecaMigrationExecutor(context);
        (await executor.InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.PendingMigrations);
        await executor.ExecuteAsync();
        (await executor.InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.CurrentVersionCompatible);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(MigrationGoldenStates.AllIds);
        (await SnapshotAsync(context)).Should().Equal(before);
        (await MigrationGoldenStates.CountRowsAsync(context, "document_parse_pages")).Should().Be(0);
    }

    [Fact]
    public async Task DownScript_RestoresExactAudioVersionAndRetainsEveryEarlierRow()
    {
        var reference = await fixture.CreateDatabaseAsync();
        try
        {
            using var previous = fixture.CreateContext(reference);
            await previous.GetService<IMigrator>().MigrateAsync(DocthecaMigrationExecutor.AddStructureAudioMigrationId);
            using var context = fixture.CreateContext(_database);
            await new DocthecaMigrationExecutor(context).ExecuteAsync();
            await SeedAllEarlierTablesAsync(context);
            var before = await SnapshotAsync(context);
            await context.Database.ExecuteSqlAsync($"""
                INSERT INTO document_parse_pages (parse_id, page_number, label, label_source)
                VALUES ({MigrationGoldenStates.ParseId}, 1, {"页一"}, {"pdf-page-labels"})
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
            (await SnapshotAsync(context)).Should().Equal(before);
            (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(MigrationGoldenStates.AllIds.Take(3));
            var tables = DocthecaMigrationExecutor.AudioVersionTableNames;
            (await MigrationGoldenStates.ReadLegacyColumnShapeAsync(context, tables)).Should().Equal(await MigrationGoldenStates.ReadLegacyColumnShapeAsync(previous, tables));
            (await MigrationGoldenStates.ReadLegacyConstraintShapeAsync(context, tables)).Should().Equal(await MigrationGoldenStates.ReadLegacyConstraintShapeAsync(previous, tables));
            (await MigrationGoldenStates.ScalarAsync<bool>(context, "SELECT to_regclass('public.document_parse_pages') IS NULL")).Should().BeTrue();
            // Inspection loads the audio migration's historical target model, not today's page table list.
            var executor = new DocthecaMigrationExecutor(context);
            (await executor.InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.PendingMigrations);
            await executor.ExecuteAsync();
            (await executor.InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.CurrentVersionCompatible);
            (await SnapshotAsync(context)).Should().Equal(before);
        }
        finally { await fixture.DropDatabaseAsync(reference); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PageTableWithoutItsHistory_IsRefused(bool noHistory)
    {
        using var context = fixture.CreateContext(_database);
        await context.GetService<IMigrator>().MigrateAsync(DocthecaMigrationExecutor.AddStructureAudioMigrationId);
        if (noHistory)
            await MigrationGoldenStates.ExecuteRawAsync(context, "DROP TABLE \"__EFMigrationsHistory\"");
        await MigrationGoldenStates.ExecuteRawAsync(context, "CREATE TABLE document_parse_pages (parse_id uuid PRIMARY KEY)");
        var executor = new DocthecaMigrationExecutor(context);
        (await executor.InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.InspectionFailed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync());
    }

    [Fact]
    public async Task WrongCurrentPageShape_AndUnknownHistoryFailClosed()
    {
        using var context = fixture.CreateContext(_database);
        var executor = new DocthecaMigrationExecutor(context);
        await executor.ExecuteAsync();
        await MigrationGoldenStates.ExecuteRawAsync(context, "ALTER TABLE document_parse_pages ALTER COLUMN label TYPE varchar(100)");
        (await executor.InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.InspectionFailed);
        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ({"29990101000000_Unknown"}, {"10.0.13"})
            """);
        (await executor.InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.VersionTooNew);
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync());
    }

    internal static async Task SeedAllEarlierTablesAsync(DocthecaDbContext context)
    {
        await MigrationGoldenStates.SeedBusinessRowsAsync(context);
        var definition = Guid.NewGuid();
        var level = Guid.NewGuid();
        var node = Guid.NewGuid();
        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO structure_definitions (id,name,created_at,updated_at) VALUES ({definition}, 'D','2026-01-01T00:00:00Z','2026-01-01T00:00:00Z');
            INSERT INTO structure_levels (id,definition_id,name,sort_order) VALUES ({level},{definition},'Unit',0);
            UPDATE document_files SET structure_definition_id={definition} WHERE id={MigrationGoldenStates.FileId};
            UPDATE document_parse_blocks SET sequence=0 WHERE id={MigrationGoldenStates.BlockId};
            """);
        if (await MigrationGoldenStates.ScalarAsync<bool>(context, "SELECT to_regclass('public.document_structure_trees') IS NOT NULL"))
        {
            context.DocumentStructureNodes.Add(new Doctheca.Database.Entities.DocumentStructureNodeEntity {
                Id=node, ParseId=MigrationGoldenStates.ParseId, LevelId=level, Name="Unit 1", StartSequence=0, EndSequence=0,
                CreatedAt=DateTimeOffset.Parse("2026-01-01T00:00:00Z"), UpdatedAt=DateTimeOffset.Parse("2026-01-01T00:00:00Z") });
            context.DocumentStructureAudio.Add(new Doctheca.Database.Entities.DocumentStructureAudioEntity {
                ParseId=MigrationGoldenStates.ParseId, NodeId=node, SourcePath="unit1/test.wav", ContentType="audio/wav", SizeBytes=10,
                ObjectKey="audio/synthetic/test.wav", MatchStatus="matched", LinkSource="manual",
                CreatedAt=DateTimeOffset.Parse("2026-01-01T00:00:00Z"), UpdatedAt=DateTimeOffset.Parse("2026-01-01T00:00:00Z") });
            await context.SaveOwnedChangesAsync();
        }
        else await context.Database.ExecuteSqlAsync($"""
            INSERT INTO document_structure_nodes (id,parse_id,level_id,name,sort_order,start_sequence,end_sequence,created_at,updated_at)
            VALUES ({node},{MigrationGoldenStates.ParseId},{level},'Unit 1',0,0,0,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z');
            INSERT INTO document_structure_audio (id,parse_id,node_id,source_path,content_type,size_bytes,object_key,match_status,link_source,created_at,updated_at)
            VALUES ({Guid.NewGuid()},{MigrationGoldenStates.ParseId},{node},'unit1/test.wav','audio/wav',10,'audio/synthetic/test.wav','matched','manual','2026-01-01T00:00:00Z','2026-01-01T00:00:00Z');
            """);
    }

    internal static async Task<List<string>> SnapshotAsync(DocthecaDbContext context)
    {
        var rows = new List<string>();
        foreach (var table in DocthecaMigrationExecutor.AudioVersionTableNames)
        {
            await context.Database.OpenConnectionAsync();
            try
            {
                await using var command = context.Database.GetDbConnection().CreateCommand();
                // Compare the historical row fields across the later additive nullable column.
                command.CommandText = $"SELECT '{table}:' || ({MigrationGoldenStates.LegacyRowJson(table)} - 'footnote' - 'required')::text FROM {table} t ORDER BY 1";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
            }
            finally { await context.Database.CloseConnectionAsync(); }
        }
        return rows;
    }
}
