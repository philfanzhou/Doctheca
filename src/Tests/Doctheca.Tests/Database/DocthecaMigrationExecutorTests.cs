using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Doctheca.Database;
using Xunit;

namespace Doctheca.Tests.Database;

/// <summary>
/// Baseline-migration and legacy-takeover behavior against real PostgreSQL (issue #52): the
/// empty state migrates to the four tables with indexes, foreign keys, the updated_at trigger,
/// and one history row; a full legacy database is taken over by safe backfills plus the verified
/// baseline registration while business data is preserved; legacy databases with missing
/// nullable columns are completed; unknown or conflicting structures (missing NOT NULL columns
/// without a default, wrong types, partial table sets, too-new history) are refused with the
/// fixed error code and zero writes; the migrated result matches the retired EnsureCreated
/// result column by column; and secret canaries never enter the error surfaces.
/// </summary>
[Collection(MigrationIntegrationCollection.Name)]
public sealed class DocthecaMigrationExecutorTests : IAsyncLifetime
{
    private readonly PostgreSqlMigrationFixture _fixture;
    private string _database = null!;

    public DocthecaMigrationExecutorTests(PostgreSqlMigrationFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => _database = await _fixture.CreateDatabaseAsync();

    public async Task DisposeAsync() => await _fixture.DropDatabaseAsync(_database);

    private DocthecaMigrationExecutor Executor(DocthecaDbContext context, ILogger? logger = null) =>
        new(context, logger ?? NullLogger.Instance);

    [Fact]
    public async Task EmptyDatabase_MigratesToBaselineContract()
    {
        using var context = _fixture.CreateContext(_database);

        (await Executor(context).InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.Empty);
        await Executor(context).ExecuteAsync();

        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should()
            .Equal(MigrationGoldenStates.InitialId);
        foreach (var table in DocthecaMigrationExecutor.KnownTableNames)
        {
            (await MigrationGoldenStates.CountRowsAsync(context, table)).Should().Be(0);
        }

        (await MigrationGoldenStates.ScalarAsync<bool>(
            context,
            $"SELECT EXISTS (SELECT 1 FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid WHERE c.relname = 'document_files' AND t.tgname = '{DocthecaMigrationExecutor.UpdatedAtTriggerName}')"))
            .Should().BeTrue();
        var constraints = await MigrationGoldenStates.ReadConstraintShapeAsync(context);
        constraints.Should().Contain(name => name.StartsWith("IX_document_files_file_name:index:"));
        constraints.Should().Contain(name => name.StartsWith("IX_document_parses_model_version:index:"));
        constraints.Should().Contain(name => name.StartsWith("FK_document_parses_document_files_document_file_id:f:"));
        constraints.Should().Contain(name => name.StartsWith("PK_document_files:p:"));

        // The second execution over the current database does nothing and stays compatible.
        (await Executor(context).InspectAsync()).Should()
            .Be(DocthecaMigrationExecutor.DatabaseState.CurrentVersionCompatible);
        await Executor(context).ExecuteAsync();
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should()
            .Equal(MigrationGoldenStates.InitialId);
    }

    [Fact]
    public async Task MissingCatalog_IsCreatedByMigration()
    {
        var database = $"missing_{Guid.NewGuid():N}";
        try
        {
            using var context = _fixture.CreateContext(database);
            (await Executor(context).InspectAsync()).Should()
                .Be(DocthecaMigrationExecutor.DatabaseState.Empty);
            await Executor(context).ExecuteAsync();
            (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should()
                .Equal(MigrationGoldenStates.InitialId);
        }
        finally
        {
            await _fixture.DropDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task FullLegacyDatabase_TakeoverStampsBaselineAndPreservesData()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.EnsureCreatedLegacyAsync(context);
        await MigrationGoldenStates.ExecuteRawAsync(context, MigrationGoldenStates.TriggerSql);
        await MigrationGoldenStates.SeedBusinessRowsAsync(context);
        var columnsBefore = await MigrationGoldenStates.ReadColumnShapeAsync(context);
        var constraintsBefore = await MigrationGoldenStates.ReadConstraintShapeAsync(context);

        (await Executor(context).InspectAsync()).Should()
            .Be(DocthecaMigrationExecutor.DatabaseState.LegacyTakeoverRequired);
        await Executor(context).ExecuteAsync();

        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should()
            .Equal(MigrationGoldenStates.InitialId);
        // Column shape is byte-for-byte unchanged by the takeover; only the (identical)
        // trigger/index backfills and the history row were written.
        (await MigrationGoldenStates.ReadColumnShapeAsync(context)).Should().Equal(columnsBefore);
        (await MigrationGoldenStates.ReadConstraintShapeAsync(context)).Should().Equal(constraintsBefore);
        await AssertBusinessRowsPreservedAsync(context);
    }

    [Fact]
    public async Task LegacyWithMissingNullableColumns_BackfillsSafelyAndStamps()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.EnsureCreatedLegacyAsync(context);
        await MigrationGoldenStates.SeedBusinessRowsAsync(context);
        // A legacy database whose historical ALTER batch never completed: nullable columns and
        // an index are absent while the data is already present.
        await MigrationGoldenStates.ExecuteRawAsync(context,
            "ALTER TABLE document_files DROP COLUMN subject");
        await MigrationGoldenStates.ExecuteRawAsync(context,
            "ALTER TABLE document_parses DROP COLUMN content_list_v2");
        await MigrationGoldenStates.ExecuteRawAsync(context,
            "DROP INDEX IF EXISTS IX_document_files_file_name");

        await Executor(context).ExecuteAsync();

        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should()
            .Equal(MigrationGoldenStates.InitialId);
        var shape = await MigrationGoldenStates.ReadColumnShapeAsync(context);
        shape.Should().Contain("document_files.subject:character varying(50):null");
        shape.Should().Contain("document_parses.content_list_v2:jsonb:null");
        var constraints = await MigrationGoldenStates.ReadConstraintShapeAsync(context);
        constraints.Should().Contain(name => name.StartsWith("IX_document_files_file_name:index:"));
        // Data is preserved; the backfilled nullable columns are NULL for the pre-existing
        // rows exactly as the "safe backfill" rule defines (no data is invented).
        (await MigrationGoldenStates.CountRowsAsync(context, "document_files")).Should().Be(1);
        (await MigrationGoldenStates.ScalarAsync<string>(
            context, $"SELECT markdown_content FROM document_parses WHERE id = '{MigrationGoldenStates.ParseId}'"))
            .Should().Be("# legacy");
        (await MigrationGoldenStates.ScalarAsync<bool>(
            context, $"SELECT subject IS NULL FROM document_files WHERE id = '{MigrationGoldenStates.FileId}'"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task LegacyWithMissingNotNullColumnWithoutDefault_IsRefused()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.EnsureCreatedLegacyAsync(context);
        await MigrationGoldenStates.SeedBusinessRowsAsync(context);
        await MigrationGoldenStates.ExecuteRawAsync(context,
            "ALTER TABLE document_parses DROP COLUMN model_version");

        var inspection = await Executor(context).InspectDetailedAsync(default);
        inspection.State.Should().Be(DocthecaMigrationExecutor.DatabaseState.InspectionFailed);
        inspection.Reason.Should().Contain("model_version");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Executor(context).ExecuteAsync());
        await AssertRefusedAsync(context, failure);
    }

    [Fact]
    public async Task LegacyWithWrongColumnType_IsRefused()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.EnsureCreatedLegacyAsync(context);
        await MigrationGoldenStates.ExecuteRawAsync(context,
            "ALTER TABLE document_parses ALTER COLUMN status TYPE character varying(5)");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Executor(context).ExecuteAsync());
        await AssertRefusedAsync(context, failure);
    }

    [Fact]
    public async Task PartiallyPresentTables_AreRefused()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.EnsureCreatedLegacyAsync(context);
        await MigrationGoldenStates.ExecuteRawAsync(context, "DROP TABLE document_parse_blocks CASCADE");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Executor(context).ExecuteAsync());
        await AssertRefusedAsync(context, failure);
    }

    [Fact]
    public async Task TooNewHistory_IsRefused()
    {
        using var context = _fixture.CreateContext(_database);
        await Executor(context).ExecuteAsync();
        await MigrationGoldenStates.ExecuteRawAsync(context,
            $"""
            INSERT INTO "{MigrationGoldenStates.HistoryTable}" ("MigrationId", "ProductVersion")
            VALUES ('20990101000000_FromTheFuture', '99.0.0')
            """);

        (await Executor(context).InspectAsync()).Should()
            .Be(DocthecaMigrationExecutor.DatabaseState.VersionTooNew);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Executor(context).ExecuteAsync());
        failure.Message.Should().Contain(DocthecaMigrationExecutor.IncompatibleErrorCode);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should()
            .HaveCount(2);
    }

    [Fact]
    public async Task MigratedResult_MatchesRetiredEnsureCreatedResult_ColumnByColumn()
    {
        var legacy = await _fixture.CreateDatabaseAsync();
        var migrated = await _fixture.CreateDatabaseAsync();
        try
        {
            using var legacyContext = _fixture.CreateContext(legacy);
            await MigrationGoldenStates.EnsureCreatedLegacyAsync(legacyContext);

            using var migratedContext = _fixture.CreateContext(migrated);
            await Executor(migratedContext).ExecuteAsync();

            (await MigrationGoldenStates.ReadColumnShapeAsync(legacyContext)).Should()
                .Equal(await MigrationGoldenStates.ReadColumnShapeAsync(migratedContext));
            (await MigrationGoldenStates.ReadConstraintShapeAsync(legacyContext)).Should()
                .Equal(await MigrationGoldenStates.ReadConstraintShapeAsync(migratedContext));
        }
        finally
        {
            await _fixture.DropDatabaseAsync(legacy);
            await _fixture.DropDatabaseAsync(migrated);
        }
    }

    [Fact]
    public async Task ConnectionFailure_IsRefusedWithoutSecretsInSurfaces()
    {
        using var context = _fixture.CreateContextWithConnectionString(
            _fixture.GetConnectionString(_database).Replace(_fixture.PasswordCanary, "definitely-wrong"));
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var executorContext = context;

        (await Executor(executorContext, loggerFactory.CreateLogger("test")).InspectAsync()).Should()
            .Be(DocthecaMigrationExecutor.DatabaseState.InspectionFailed);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Executor(executorContext, loggerFactory.CreateLogger("test")).ExecuteAsync());
        failure.Message.Should().Contain(DocthecaMigrationExecutor.IncompatibleErrorCode);

        var surfaces = failure.Message + Environment.NewLine + string.Join(Environment.NewLine, logs.Messages);
        surfaces.Should().NotContain(_fixture.PasswordCanary);
        surfaces.Should().NotContain("Host=");
    }

    private static async Task AssertBusinessRowsPreservedAsync(DocthecaDbContext context)
    {
        (await MigrationGoldenStates.CountRowsAsync(context, "document_files")).Should().Be(1);
        (await MigrationGoldenStates.CountRowsAsync(context, "document_parses")).Should().Be(1);
        (await MigrationGoldenStates.CountRowsAsync(context, "document_parse_images")).Should().Be(1);
        (await MigrationGoldenStates.CountRowsAsync(context, "document_parse_blocks")).Should().Be(1);
        (await MigrationGoldenStates.ScalarAsync<string>(
            context, $"SELECT subject FROM document_files WHERE id = '{MigrationGoldenStates.FileId}'"))
            .Should().Be("math");
        (await MigrationGoldenStates.ScalarAsync<string>(
            context, $"SELECT markdown_content FROM document_parses WHERE id = '{MigrationGoldenStates.ParseId}'"))
            .Should().Be("# legacy");
    }

    private static async Task AssertRefusedAsync(DocthecaDbContext context, InvalidOperationException failure)
    {
        failure.Message.Should().Contain(DocthecaMigrationExecutor.IncompatibleErrorCode);
        failure.Message.Should().NotContain("Host=");
        failure.Message.Should().NotContain("SELECT");
        // Refused states perform zero writes: no history table was created or stamped.
        var stamped = await MigrationGoldenStates.ScalarAsync<bool>(
            context,
            $"SELECT to_regclass('public.\"{MigrationGoldenStates.HistoryTable}\"') IS NOT NULL");
        stamped.Should().BeFalse();
    }
}
