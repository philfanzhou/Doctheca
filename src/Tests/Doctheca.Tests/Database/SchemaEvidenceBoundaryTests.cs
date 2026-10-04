using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Doctheca.Database;
using Xunit;

namespace Doctheca.Tests.Database;

[Collection(MigrationIntegrationCollection.Name)]
public sealed class SchemaEvidenceBoundaryTests(PostgreSqlMigrationFixture fixture) : IAsyncLifetime
{
    private string _database = null!;
    public async Task InitializeAsync() => _database = await fixture.CreateDatabaseAsync();
    public Task DisposeAsync() => fixture.DropDatabaseAsync(_database);

    [Fact]
    public async Task SharedColumnsAreReadNormallyAndLocalColumnsOnlyForProvenModelGap()
    {
        using var context = fixture.CreateContext(_database);
        var executor = new DocthecaMigrationExecutor(context, NullLogger.Instance);
        await executor.ExecuteAsync();
        await MigrationGoldenStates.ExecuteRawAsync(context, "CREATE EXTENSION pg_stat_statements");
        foreach (var fallback in new[] { false, true })
        {
            if (fallback) await MigrationGoldenStates.ExecuteRawAsync(context, "CREATE INDEX extra_same_shape ON document_files(file_name)");
            await MigrationGoldenStates.ScalarAsync<object>(context, "SELECT pg_stat_statements_reset()");
            Assert.Equal(DocthecaMigrationExecutor.DatabaseState.CurrentVersionCompatible, await executor.InspectAsync());
            // PostgreSQL observes the actual SQL execution, rather than a product test flag.
            var sharedCount = await MigrationGoldenStates.ScalarAsync<long>(context,
                "SELECT COALESCE(sum(calls), 0)::bigint FROM pg_stat_statements WHERE query LIKE 'SELECT n.nspname AS schema_name, tbl.relname AS table_name, a.attname AS column_name,%'");
            var localCount = await MigrationGoldenStates.ScalarAsync<long>(context,
                "SELECT COALESCE(sum(calls), 0)::bigint FROM pg_stat_statements WHERE query LIKE 'SELECT c.relname, a.attname, format_type%'");
            Assert.Equal(1, sharedCount);
            Assert.Equal(fallback ? 1 : 0, localCount);
        }
    }

    [Fact]
    public async Task ExecuteRechecksInsteadOfStampingAnEarlierCompatibleInspection()
    {
        using var context = fixture.CreateContext(_database);
        await MigrationGoldenStates.EnsureCreatedLegacyAsync(context);
        var executor = new DocthecaMigrationExecutor(context, NullLogger.Instance);
        Assert.Equal(DocthecaMigrationExecutor.DatabaseState.LegacyTakeoverRequired, await executor.InspectAsync());
        await MigrationGoldenStates.ExecuteRawAsync(context, "ALTER TABLE document_files ALTER COLUMN file_name TYPE text");
        var shape = await MigrationGoldenStates.ReadColumnShapeAsync(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync());
        Assert.Equal(shape, await MigrationGoldenStates.ReadColumnShapeAsync(context));
        Assert.False(await HistoryExists(context));
    }

    [Fact]
    public async Task EntryCancellationDoesNotReadOrWriteOrStamp()
    {
        using var context = fixture.CreateContext(_database);
        await MigrationGoldenStates.EnsureCreatedLegacyAsync(context);
        var shape = await MigrationGoldenStates.ReadColumnShapeAsync(context);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var executor = new DocthecaMigrationExecutor(context, NullLogger.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.InspectAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(cancellation.Token));
        Assert.False(await HistoryExists(context));
        Assert.Equal(shape, await MigrationGoldenStates.ReadColumnShapeAsync(context));
    }

    [Fact]
    public async Task CancellationAfterCommittedBackfillStopsBeforeHistoryWriter()
    {
        using var seed = fixture.CreateContext(_database);
        await MigrationGoldenStates.EnsureCreatedLegacyAsync(seed);
        await MigrationGoldenStates.ExecuteRawAsync(seed, "ALTER TABLE document_files DROP COLUMN subject");
        using var cancellation = new CancellationTokenSource();
        using var context = new DocthecaDbContext(new DbContextOptionsBuilder<DocthecaDbContext>()
            .UseNpgsql(fixture.GetConnectionString(_database)).AddInterceptors(new CancelBackfill(cancellation)).Options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DocthecaMigrationExecutor(context, NullLogger.Instance).ExecuteAsync(cancellation.Token));
        Assert.False(await HistoryExists(seed));
        // The completed DDL is truthful pending state, not rolled back or reported stamped.
        Assert.Contains("document_files.subject:character varying(50):null", await MigrationGoldenStates.ReadColumnShapeAsync(seed));
        await new DocthecaMigrationExecutor(seed, NullLogger.Instance).ExecuteAsync();
        Assert.Equal(new[] { MigrationGoldenStates.InitialId }, await MigrationGoldenStates.ReadAppliedHistoryAsync(seed));
    }

    [Fact]
    public async Task WriterTransactionCancellationRollsBackNewHistoryTable()
    {
        using var seed = fixture.CreateContext(_database);
        await MigrationGoldenStates.EnsureCreatedLegacyAsync(seed);
        await MigrationGoldenStates.ExecuteRawAsync(seed, """
            CREATE FUNCTION pause_history_creation() RETURNS event_trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_sleep(10); END; $$;
            CREATE EVENT TRIGGER pause_history ON ddl_command_end WHEN TAG IN ('CREATE TABLE') EXECUTE FUNCTION pause_history_creation();
            """);
        using var context = fixture.CreateContext(_database);
        using var cancellation = new CancellationTokenSource();
        var pending = new DocthecaMigrationExecutor(context, NullLogger.Instance).ExecuteAsync(cancellation.Token);
        var stopwatch = Stopwatch.StartNew();
        var entered = false;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            entered = await MigrationGoldenStates.ScalarAsync<bool>(seed,
                "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND wait_event = 'PgSleep')");
            if (entered) break;
            await Task.Delay(20);
        }
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(entered, "The history create statement never entered its transaction.");
        Assert.False(await HistoryExists(seed));
    }

    [Fact]
    public async Task HistoryShapeAndIdempotentRowMatchOriginalStamp()
    {
        using var context = fixture.CreateContext(_database);
        await MigrationGoldenStates.EnsureCreatedLegacyAsync(context);
        var executor = new DocthecaMigrationExecutor(context, NullLogger.Instance);
        await executor.ExecuteAsync(); await executor.ExecuteAsync();
        Assert.Equal(new[] { MigrationGoldenStates.InitialId }, await MigrationGoldenStates.ReadAppliedHistoryAsync(context));
        Assert.Equal("MigrationId:character varying(150):true,ProductVersion:character varying(32):true",
            await MigrationGoldenStates.ScalarAsync<string>(context, """
                SELECT string_agg(a.attname || ':' || format_type(a.atttypid, a.atttypmod) || ':' || a.attnotnull::text, ',' ORDER BY a.attnum)
                FROM pg_attribute a WHERE a.attrelid = 'public."__EFMigrationsHistory"'::regclass AND a.attnum > 0 AND NOT a.attisdropped
                """));
        Assert.Equal("PK___EFMigrationsHistory", await MigrationGoldenStates.ScalarAsync<string>(context,
            "SELECT conname FROM pg_constraint WHERE conrelid = 'public.\"__EFMigrationsHistory\"'::regclass AND contype = 'p'"));
    }

    [Fact]
    public async Task WriterFailureDoesNotInsertHistoryOrDiscloseCredentials()
    {
        using var context = fixture.CreateContext(_database);
        await MigrationGoldenStates.EnsureCreatedLegacyAsync(context);
        await MigrationGoldenStates.ExecuteRawAsync(context, """
            CREATE TABLE "__EFMigrationsHistory" (
              "MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL CHECK("ProductVersion" = 'never-matches'));
            """);
        var logs = new CapturingLoggerProvider();
        using var logger = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var failure = await Assert.ThrowsAsync<PostgresException>(() => new DocthecaMigrationExecutor(context, logger.CreateLogger("probe")).ExecuteAsync());
        Assert.Equal("23514", failure.SqlState);
        Assert.Empty(await MigrationGoldenStates.ReadAppliedHistoryAsync(context));
        var surfaces = failure.Message + string.Join('\n', logs.Messages);
        Assert.DoesNotContain(fixture.PasswordCanary, surfaces);
        Assert.DoesNotContain("Host=", surfaces);
        Assert.DoesNotContain("INSERT INTO", surfaces);
    }

    [Fact]
    public async Task CatalogPermissionFailureWithDuplicateShapeIsStillRefused()
    {
        using var seed = fixture.CreateContext(_database);
        await new DocthecaMigrationExecutor(seed, NullLogger.Instance).ExecuteAsync();
        var role = "reader_" + Guid.NewGuid().ToString("N");
        var password = Guid.NewGuid().ToString("N");
        try
        {
            await MigrationGoldenStates.ExecuteRawAsync(seed,
                $"CREATE ROLE {role} LOGIN PASSWORD '{password}'; GRANT SELECT ON \"__EFMigrationsHistory\" TO {role}; REVOKE SELECT ON pg_catalog.pg_constraint FROM PUBLIC; CREATE INDEX extra_same_shape ON document_files(file_name)");
            var connection = new NpgsqlConnectionStringBuilder(fixture.GetConnectionString(_database)) { Username = role, Password = password };
            using var context = fixture.CreateContextWithConnectionString(connection.ConnectionString);
            var logs = new CapturingLoggerProvider();
            using var logger = LoggerFactory.Create(builder => builder.AddProvider(logs));
            var executor = new DocthecaMigrationExecutor(context, logger.CreateLogger("probe"));
            Assert.Equal(DocthecaMigrationExecutor.DatabaseState.InspectionFailed, await executor.InspectAsync());
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync());
            var surfaces = failure.Message + string.Join('\n', logs.Messages);
            Assert.DoesNotContain(password, surfaces); Assert.DoesNotContain(fixture.PasswordCanary, surfaces); Assert.DoesNotContain("Host=", surfaces);
            Assert.Equal(new[] { MigrationGoldenStates.InitialId }, await MigrationGoldenStates.ReadAppliedHistoryAsync(seed));
        }
        finally
        {
            await MigrationGoldenStates.ExecuteRawAsync(seed, $"GRANT SELECT ON pg_catalog.pg_constraint TO PUBLIC; DROP OWNED BY {role}; DROP ROLE {role}");
        }
    }

    [Fact]
    public async Task UntrustedDriverPayloadNeverEntersProductDiagnostics()
    {
        var canary = "synthetic-password-" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(fixture.GetConnectionString(_database)) { Password = "" };
        var builder = new NpgsqlDataSourceBuilder(connection.ConnectionString);
        builder.UsePasswordProvider(_ => throw new InvalidOperationException(canary),
            (_, _) => throw new InvalidOperationException(canary));
        await using var source = builder.Build();
        using var context = new DocthecaDbContext(new DbContextOptionsBuilder<DocthecaDbContext>().UseNpgsql(source).Options);
        var logs = new CapturingLoggerProvider();
        using var logger = LoggerFactory.Create(logging => logging.AddProvider(logs));
        var executor = new DocthecaMigrationExecutor(context, logger.CreateLogger("probe"));
        Assert.Equal(DocthecaMigrationExecutor.DatabaseState.InspectionFailed, await executor.InspectAsync());
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync());
        var surfaces = failure.Message + string.Join('\n', logs.Messages);
        Assert.DoesNotContain(canary, surfaces); Assert.DoesNotContain("Host=", surfaces);
        Assert.NotEmpty(logs.Messages);
    }

    private static Task<bool> HistoryExists(DocthecaDbContext context) => MigrationGoldenStates.ScalarAsync<bool>(context,
        "SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL");

    private sealed class CancelBackfill(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("ALTER TABLE document_files ADD COLUMN", StringComparison.Ordinal)) cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }
}
