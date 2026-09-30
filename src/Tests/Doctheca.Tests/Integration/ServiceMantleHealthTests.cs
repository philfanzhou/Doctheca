using System.Data.Common;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ServiceMantle.Health;
using Testcontainers.PostgreSql;
using Xunit;

namespace Doctheca.Tests.Integration;

/// <summary>
/// ServiceMantle health probes on the real host: <c>/health/live</c> answers 200
/// <c>{"status":"live"}</c> without ever touching the database; <c>/health/ready</c> and the
/// <c>/health</c> alias project exactly the library-fixed
/// status/phase/migrationStatus/databaseStatus/errorCode fields from the consumer-owned
/// snapshot source (startup receipt + read-only zero-row probe of the EF-mapped tables).
/// Readiness fails closed on a missing source, a missing mapped column, an unreachable
/// database, and a probe timeout; caller cancellation propagates instead of faking health;
/// every request re-samples so a recovered database is observed immediately; all three
/// endpoints stay anonymous and answer JSON (never the SPA page) while protected APIs keep
/// returning 401. Destructive schema/outage scenarios run against one-off dedicated
/// PostgreSQL containers, never the shared fixture.
/// </summary>
[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed class ServiceMantleHealthTests : ServiceMantleIntegrationTestBase
{
    private const string LiveBody = "{\"status\":\"live\"}";

    private const string ReadyBody =
        "{\"status\":\"ready\",\"phase\":\"completed\",\"migrationStatus\":\"succeeded\"," +
        "\"databaseStatus\":\"reachable\",\"errorCode\":null}";

    private const string ProbeFailedBody =
        "{\"status\":\"not_ready\",\"phase\":null,\"migrationStatus\":null," +
        "\"databaseStatus\":null,\"errorCode\":\"health.probe_failed\"}";

    private const string ProbeTimeoutBody =
        "{\"status\":\"not_ready\",\"phase\":null,\"migrationStatus\":null," +
        "\"databaseStatus\":null,\"errorCode\":\"health.probe_timeout\"}";

    private const string SchemaUnavailableBody =
        "{\"status\":\"not_ready\",\"phase\":\"completed\",\"migrationStatus\":\"failed\"," +
        "\"databaseStatus\":\"reachable\",\"errorCode\":\"doctheca.schema_unavailable\"}";

    public ServiceMantleHealthTests(PostgreSqlFixture database) : base(database)
    {
    }

    [Fact]
    public async Task LiveEndpoint_IsAnonymous_ReturnsLiveJson_NotTheSpaPage()
    {
        var contentRoot = CreateTempContentRoot(withWwwrootStub: true);
        try
        {
            using var factory = CreateFactory(contentRoot: contentRoot);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/health/live");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(LiveBody, await response.Content.ReadAsStringAsync());
            // Every response — health included — carries the correlation id of the first
            // middleware.
            Assert.Single(response.Headers.GetValues(CorrelationHeaderName));

            // The SPA contract is untouched: deep links still serve index.html anonymously.
            using var spa = await client.GetAsync("/documents/library");
            Assert.Equal(HttpStatusCode.OK, spa.StatusCode);
            Assert.Equal("text/html", spa.Content.Headers.ContentType?.MediaType);
            Assert.Contains("doctheca-stub-index-marker", await spa.Content.ReadAsStringAsync());

            // The business authorization boundary is untouched.
            using var api = await client.GetAsync(ProtectedApiRoute);
            Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ReadinessEndpoints_HealthyDatabase_ProjectReady_WithoutTouchingRows()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var countsBefore = await CountAllMappedTables();

        foreach (var route in new[] { "/health/ready", "/health" })
        {
            using var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(ReadyBody, await response.Content.ReadAsStringAsync());
        }

        // The probe is read-only: no row counts change and no DDL runs.
        var countsAfter = await CountAllMappedTables();
        Assert.Equal(countsBefore, countsAfter);
    }

    [Fact]
    public async Task ReadinessEndpoints_MissingSource_FailClosed()
    {
        using var factory = CreateFactory(configureTestServices: services =>
            services.RemoveAll<IServiceHealthSnapshotSource>());
        using var client = factory.CreateClient();

        foreach (var route in new[] { "/health/ready", "/health" })
        {
            using var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(ProbeFailedBody, await response.Content.ReadAsStringAsync());
        }

        // Liveness never resolves the source.
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(LiveBody, await live.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DedicatedDatabase_EmptyThenCurrentStartup_MissingColumn_FailsClosedAndRecovers()
    {
        await using var container = await StartDedicatedDatabaseAsync();
        var settings = DbConfigOverrides(container.GetConnectionString());

        // Empty database: the first host run creates the schema and completes the receipt.
        using (var firstFactory = CreateFactory(settings: settings))
        using (var firstClient = firstFactory.CreateClient())
        {
            using var ready = await firstClient.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.Equal(ReadyBody, await ready.Content.ReadAsStringAsync());
        }

        // Current/legacy database: a later host start finds the existing schema (the
        // initializer skips creation) and readiness still passes.
        using var factory = CreateFactory(settings: settings);
        using var client = factory.CreateClient();
        using (var ready = await client.GetAsync("/health/ready"))
        {
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        // A mapped column disappears after initialization (the legacy initializer swallows
        // some ALTER failures, so this must never be reported as ready).
        var (tableName, columnName, columnType) =
            await PickMappedColumnAsync(container.GetConnectionString(), "document_parses");
        await ExecuteAsync(
            container.GetConnectionString(),
            $"ALTER TABLE {Quote(tableName)} DROP COLUMN {Quote(columnName)}");

        using (var notReady = await client.GetAsync("/health/ready"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
            Assert.Equal(SchemaUnavailableBody, await notReady.Content.ReadAsStringAsync());
        }

        // The alias follows the same projection; liveness never queries the database.
        using (var alias = await client.GetAsync("/health"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, alias.StatusCode);
        }

        using (var live = await client.GetAsync("/health/live"))
        {
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Equal(LiveBody, await live.Content.ReadAsStringAsync());
        }

        // Restore the column: the very next request re-samples and is ready again.
        await ExecuteAsync(
            container.GetConnectionString(),
            $"ALTER TABLE {Quote(tableName)} ADD COLUMN {Quote(columnName)} {columnType}");

        using (var recovered = await client.GetAsync("/health/ready"))
        {
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            Assert.Equal(ReadyBody, await recovered.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task DatabaseOutage_ReadyFailsClosed_LiveStaysUp_RecoversAfterRestart()
    {
        await using var container = await StartDedicatedDatabaseAsync();
        var settings = DbConfigOverrides(container.GetConnectionString());
        using var factory = CreateFactory(settings: settings);
        using var client = factory.CreateClient();

        using (var ready = await client.GetAsync("/health/ready"))
        {
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.Equal(ReadyBody, await ready.Content.ReadAsStringAsync());
        }

        await container.StopAsync();
        try
        {
            // Fail closed while the database is down: 503 + not_ready with a safe code and no
            // exception content. docker stop closes the published port, so the probe's connect
            // is refused and maps to doctheca.database_unreachable; a slower teardown that
            // exhausts the 3s probe budget, or an unclassified surfacing, maps to the library's
            // own health.probe_timeout / health.probe_failed — all honest fail-closed outcomes,
            // never a fake ready.
            using var notReady = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
            var outageBody = await notReady.Content.ReadAsStringAsync();
            using (var document = System.Text.Json.JsonDocument.Parse(outageBody))
            {
                var root = document.RootElement;
                Assert.Equal("not_ready", root.GetProperty("status").GetString());
                Assert.Contains(
                    root.GetProperty("errorCode").GetString(),
                    new[]
                    {
                        Doctheca.Host.Health.DocthecaHealthSnapshotSource.DatabaseUnreachableErrorCode,
                        "health.probe_timeout",
                        "health.probe_failed"
                    });
                // The snapshot never claims full readiness: either the database state is
                // unreachable (receipt keeps migrationStatus=succeeded) or the projection is
                // the value-free library failure (all snapshot fields null).
                Assert.NotEqual(
                    "reachable",
                    root.GetProperty("databaseStatus").GetString());
            }

            // Live does not resolve the source or query the database: it stays 200 while the
            // database is down.
            using var live = await client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Equal(LiveBody, await live.Content.ReadAsStringAsync());
        }
        finally
        {
            await container.StartAsync();
        }

        // Recovery: Testcontainers republishes the container on a fresh host port on restart,
        // so a host is pointed at the restored database through a new factory. It reports ready
        // again, proving readiness tracks the live database state rather than a latched failure.
        // Same-process re-sampling after an in-place schema fault is proven separately by
        // DedicatedDatabase_EmptyThenCurrentStartup_MissingColumn_FailsClosedAndRecovers.
        var restartedSettings = DbConfigOverrides(container.GetConnectionString());
        using var recoveredFactory = CreateFactory(settings: restartedSettings);
        using var recoveredClient = recoveredFactory.CreateClient();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        var lastStatus = HttpStatusCode.ServiceUnavailable;
        var lastBody = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            using var attempt = await recoveredClient.GetAsync("/health/ready");
            lastStatus = attempt.StatusCode;
            lastBody = await attempt.Content.ReadAsStringAsync();
            if (attempt.StatusCode == HttpStatusCode.OK)
            {
                break;
            }

            await Task.Delay(250);
        }

        Assert.True(
            lastStatus == HttpStatusCode.OK,
            $"readiness never recovered against the restarted database: {lastStatus} {lastBody}");
        Assert.Equal(ReadyBody, lastBody);
    }

    [Fact]
    public async Task ProbeTimeout_FailsClosed_WithLibrarySafeCode()
    {
        using var factory = CreateFactory(configureTestServices: services =>
        {
            services.RemoveAll<IServiceHealthSnapshotSource>();
            services.AddScoped<IServiceHealthSnapshotSource>(
                _ => new BlockingSnapshotSource());
        });
        using var client = factory.CreateClient();

        // The host ProbeTimeout is 3 seconds; the blocking source never answers in time.
        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ProbeTimeoutBody, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CallerCancellation_Propagates_InsteadOfFakingHealth()
    {
        var source = new CancellationObservingSnapshotSource();
        using var factory = CreateFactory(configureTestServices: services =>
        {
            services.RemoveAll<IServiceHealthSnapshotSource>();
            services.AddScoped<IServiceHealthSnapshotSource>(_ => source);
        });
        using var client = factory.CreateClient();
        using var cts = new CancellationTokenSource();

        var request = client.GetAsync("/health/ready", cts.Token);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        // The caller observes its own cancellation — no 200, no 500, no fake health — and the
        // source observed the cancellation on the token it received.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await source.ObservedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ConcurrentReadinessRequests_GetIndependentSnapshots()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            client.GetAsync("/health/ready")));

        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(ReadyBody, await response.Content.ReadAsStringAsync());
            }
        }
    }

    private async Task<Dictionary<string, long>> CountAllMappedTables()
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        foreach (var table in new[]
                 {
                     "document_files", "document_parses", "document_parse_images",
                     "document_parse_blocks"
                 })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {Quote(table)}";
            counts[table] = (long)(await command.ExecuteScalarAsync())!;
        }

        return counts;
    }

    private static async Task<PostgreSqlContainer> StartDedicatedDatabaseAsync()
    {
        var container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("doctheca")
            .WithUsername("postgres")
            .WithPassword(Guid.NewGuid().ToString("N"))
            .Build();
        await container.StartAsync();
        return container;
    }

    private static IReadOnlyDictionary<string, string?> DbConfigOverrides(string connectionString)
    {
        var connection = new DbConnectionStringBuilder { ConnectionString = connectionString };
        return new Dictionary<string, string?>
        {
            ["PostgreSql:Host"] = Convert.ToString(connection["Host"]),
            ["PostgreSql:Port"] = Convert.ToString(connection["Port"]),
            ["PostgreSql:Username"] = Convert.ToString(connection["Username"]),
            ["PostgreSql:Password"] = Convert.ToString(connection["Password"]),
            ["Database:Name"] = "doctheca"
        };
    }

    /// <summary>
    /// Picks one ordinary mapped column (not the id primary key, no user-defined type) and
    /// returns its table, name, and exact DDL type via <c>format_type</c>.
    /// </summary>
    private static async Task<(string Table, string Column, string Type)> PickMappedColumnAsync(
        string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.attname, format_type(a.atttypid, a.atttypmod)
            FROM pg_attribute a
            WHERE a.attrelid = $1::regclass
              AND a.attnum > 0
              AND NOT a.attisdropped
              AND a.attname <> 'id'
            ORDER BY a.attnum
            LIMIT 1
            """;
        command.Parameters.Add(new NpgsqlParameter { Value = table });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"no mapped column found on {table}");
        return (table, reader.GetString(0), reader.GetString(1));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <summary>Never completes until the library's probe budget cancels the token.</summary>
    private sealed class BlockingSnapshotSource : IServiceHealthSnapshotSource
    {
        public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>Signals when the probe started and when it observed the cancellation.</summary>
    private sealed class CancellationObservingSnapshotSource : IServiceHealthSnapshotSource
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ObservedCancellation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("unreachable");
        }
    }
}
