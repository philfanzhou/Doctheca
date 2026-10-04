using System.Data.Common;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle.Migration;
using Npgsql;
using Doctheca.Host;
using Xunit;

namespace Doctheca.Tests.Database;

/// <summary>
/// End-to-end startup behavior of the three-phase database initialization in the real
/// Program.cs host (issue #53): a missing target database refuses startup by default with the
/// fixed <c>database_target_preparation.creation_not_allowed</c> code and zero writes;
/// <c>Database:AllowCreate=true</c> creates the missing database and the migration orchestration
/// then applies the baseline and publishes readiness as
/// <c>migrationStatus=succeeded</c>; an existing database starts straight into orchestration; a
/// non-boolean switch value fails startup fast. All hosts run against the shared container with
/// hermetic Consul/Loki/OpenSearch/StructaDoc/LLM settings; the password canary never appears in
/// any surfaced exception.
/// </summary>
[Collection(MigrationIntegrationCollection.Name)]
public sealed class StartupMigrationHostTests
{
    private const string ReadyBody =
        "{\"status\":\"ready\",\"phase\":\"completed\",\"migrationStatus\":\"succeeded\"," +
        "\"databaseStatus\":\"reachable\",\"errorCode\":null}";

    private readonly PostgreSqlMigrationFixture _fixture;

    public StartupMigrationHostTests(PostgreSqlMigrationFixture fixture)
    {
        _fixture = fixture;
    }

    // ---------- missing database: default refusal ----------

    [Fact]
    public async Task Host_Startup_MissingDatabase_Default_RefusesWithFixedCode_ZeroWrites()
    {
        var missing = NewDatabaseName();
        using var factory = CreateHostFactory(missing);

        // The refusal surfaces while the host is being built; the exact wrapper around the
        // startup exception is host infrastructure, so the assertion checks the fixed gate message and safe error code.
        var exception = Record.Exception(() => factory.CreateClient());

        exception.Should().NotBeNull();
        exception!.ToString().Should().Contain("database_target_preparation.creation_not_allowed");
        exception!.ToString().Should().NotContain(_fixture.PasswordCanary);

        // Zero writes: the missing catalog was not created and nothing else was touched.
        (await DatabaseExistsAsync(missing)).Should().BeFalse();
    }

    // ---------- missing database: allowed creation ----------

    [Fact]
    public async Task Host_Startup_MissingDatabase_AllowCreateTrue_CreatesMigratesAndReportsReady()
    {
        var missing = NewDatabaseName();
        try
        {
            using var factory = CreateHostFactory(missing, ("Database:AllowCreate", "true"));
            using var client = factory.CreateClient();

            using var ready = await client.GetAsync("/health/ready");
            ready.StatusCode.Should().Be(HttpStatusCode.OK);
            (await ready.Content.ReadAsStringAsync()).Should().Be(ReadyBody);

            (await DatabaseExistsAsync(missing)).Should().BeTrue();
            (await ReadHistoryAsync(missing)).Should().Equal(
                Doctheca.Database.DocthecaMigrationExecutor.InitialCreateMigrationId);
            foreach (var table in Doctheca.Database.DocthecaMigrationExecutor.KnownTableNames)
            {
                (await CountRowsAsync(missing, table)).Should().Be(0,
                    $"table '{table}' must exist after startup");
            }
        }
        finally
        {
            await DropDatabaseAsync(missing);
        }
    }

    // ---------- existing database ----------

    [Fact]
    public async Task Host_Startup_ExistingDatabase_MigratesUnderOrchestration_AndReportsReady()
    {
        var database = await _fixture.CreateDatabaseAsync();
        try
        {
            using var factory = CreateHostFactory(database);
            using var client = factory.CreateClient();

            using var ready = await client.GetAsync("/health/ready");
            ready.StatusCode.Should().Be(HttpStatusCode.OK);
            (await ready.Content.ReadAsStringAsync()).Should().Be(ReadyBody);

            (await ReadHistoryAsync(database)).Should().Equal(
                Doctheca.Database.DocthecaMigrationExecutor.InitialCreateMigrationId);
        }
        finally
        {
            await _fixture.DropDatabaseAsync(database);
        }
    }

    // ---------- configuration validation ----------

    [Fact]
    public async Task Host_Startup_InvalidAllowCreateBoolean_FailsFast()
    {
        var missing = NewDatabaseName();
        using var factory = CreateHostFactory(missing, ("Database:AllowCreate", "maybe"));

        var exception = Record.Exception(() => factory.CreateClient());

        exception.Should().NotBeNull();
        exception!.ToString().Should().Contain("database_target_preparation.invalid_target");
        exception!.ToString().Should().NotContain(_fixture.PasswordCanary);

        (await DatabaseExistsAsync(missing)).Should().BeFalse();
    }

    [Theory]
    [InlineData("PostgreSql:Port", "1")]
    [InlineData("PostgreSql:Password", "fictitious-wrong-password")]
    public async Task Host_Startup_ConnectionOrAuthenticationFailure_RefusesBeforeListening(string key, string value)
    {
        var missing = NewDatabaseName();
        using var factory = CreateHostFactory(missing, ("Database:AllowCreate", "true"), (key, value));
        var exception = Record.Exception(() => factory.CreateClient());
        exception.Should().NotBeNull();
        exception!.ToString().Should().Contain("Database startup gate failed")
            .And.Contain("database_target_preparation.")
            .And.NotContain(_fixture.PasswordCanary);
        if (key == "PostgreSql:Password") exception.ToString().Should().NotContain(value);
        (await DatabaseExistsAsync(missing)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, "migration.execution_failed")]
    [InlineData(false, "migration.final_state_invalid")]
    public async Task Host_Startup_MigrationOrFinalInspectionFailure_RefusesBeforeListening(bool executionFailure, string expected)
    {
        var database = await _fixture.CreateDatabaseAsync();
        try
        {
            using var factory = CreateHostFactory(database).WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IDatabaseMigrationExecutor>();
                    services.AddScoped<IDatabaseMigrationExecutor>(_ => new RefusingExecutor(executionFailure, _fixture.PasswordCanary));
                }));
            var exception = Record.Exception(() => factory.CreateClient());
            exception.Should().NotBeNull();
            exception!.ToString().Should().Contain(expected).And.NotContain(_fixture.PasswordCanary);
        }
        finally { await _fixture.DropDatabaseAsync(database); }
    }

    private sealed class RefusingExecutor(bool executionFailure, string canary) : IDatabaseMigrationExecutor
    {
        public ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(MigrationObservationState.Empty);
        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default) => executionFailure
            ? ValueTask.FromException(new InvalidOperationException(canary)) : ValueTask.CompletedTask;
    }

    // ---------- helpers ----------

    private string NewDatabaseName() => $"host_{Guid.NewGuid():N}";

    /// <summary>
    /// Builds a factory around the real Program.cs entry point with hermetic settings: Consul
    /// points at a closed loopback port (deterministic fallback to the settings below), the
    /// empty Loki URI disables the remote sink, OpenSearch/StructaDoc point at closed ports,
    /// the LLM stays disabled, and the IdentityService values mirror the synthetic Testing
    /// configuration. None of them are real credentials.
    /// </summary>
    private WebApplicationFactory<Program> CreateHostFactory(
        string database, params (string Key, string Value)[] extraSettings)
    {
        var connection = new DbConnectionStringBuilder
        {
            ConnectionString = _fixture.GetConnectionString("postgres"),
        };
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Consul:Host", "127.0.0.1");
            builder.UseSetting("Consul:Port", "1");
            builder.UseSetting("Consul:EnableCache", "false");
            builder.UseSetting("PostgreSql:Host", Convert.ToString(connection["Host"]));
            builder.UseSetting("PostgreSql:Port", Convert.ToString(connection["Port"]));
            builder.UseSetting("PostgreSql:Username", Convert.ToString(connection["Username"]));
            builder.UseSetting("PostgreSql:Password", Convert.ToString(connection["Password"]));
            builder.UseSetting("Database:Name", database);
            builder.UseSetting("Loki:Uri", "");
            builder.UseSetting("OpenSearch:Url", "http://127.0.0.1:1");
            builder.UseSetting("OpenSearch:IndexName", "doctheca-startup-tests");
            builder.UseSetting("StructaDoc:BaseUrl", "");
            builder.UseSetting("LlmDocumentAnalysis:ApiKey", "");
            builder.UseSetting("IdentityService:Authority", "http://localhost:5002");
            builder.UseSetting("IdentityService:Issuer", "http://localhost:5002");
            builder.UseSetting("IdentityService:AdditionalValidIssuers:0", "QuantumZhou.Identity");
            builder.UseSetting("IdentityService:Audience", "QuantumZhou.microservices");
            builder.UseSetting("IdentityService:RequireHttpsMetadata", "false");
            builder.UseSetting("IdentityService:ClockSkewSeconds", "30");
            builder.UseSetting("IdentityService:AppId", "doctheca-test-app");
            builder.UseSetting("IdentityService:AppSecret", "doctheca-test-secret");
            foreach (var (key, value) in extraSettings)
            {
                builder.UseSetting(key, value);
            }
        });
    }

    private async Task<bool> DatabaseExistsAsync(string database)
    {
        await using var connection = new NpgsqlConnection(_fixture.GetConnectionString("postgres"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name";
        command.Parameters.AddWithValue("@name", database);
        return await command.ExecuteScalarAsync() is not null;
    }

    private async Task DropDatabaseAsync(string database)
    {
        await using var connection = new NpgsqlConnection(_fixture.GetConnectionString("postgres"));
        await connection.OpenAsync();
        await using var terminate = connection.CreateCommand();
        terminate.CommandText = """
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = @name AND pid <> pg_backend_pid()
            """;
        terminate.Parameters.AddWithValue("@name", database);
        await terminate.ExecuteNonQueryAsync();
        await using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{database}\"";
        await drop.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> ReadHistoryAsync(string database)
    {
        await using var connection = new NpgsqlConnection(_fixture.GetConnectionString(database));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"";
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private async Task<long> CountRowsAsync(string database, string table)
    {
        await using var connection = new NpgsqlConnection(_fixture.GetConnectionString(database));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{table}\"";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
