using Doctheca.Database;
using Doctheca.Host;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ServiceMantle.Health;
using ServiceMantle.Migration;
using Xunit;

namespace Doctheca.Tests.Database;

/// <summary>Production registrations and the direct shared gate against real PostgreSQL.</summary>
[Collection(MigrationIntegrationCollection.Name)]
public sealed class DatabaseTargetPreparationTests(PostgreSqlMigrationFixture fixture) : IAsyncLifetime
{
    private string database = null!;
    public async Task InitializeAsync() => database = await fixture.CreateDatabaseAsync();
    public Task DisposeAsync() => fixture.DropDatabaseAsync(database);

    private static IConfiguration Config(string? allowCreate) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:AllowCreate"] = allowCreate,
        }).Build();

    internal static ServiceProvider BuildServices(string connectionString, IDatabaseMigrationExecutor? executor = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDocthecaStartupDatabase(services.AddDocthecaServiceMantleFoundation());
        services.AddDbContext<DocthecaDbContext>(o => o.UseNpgsql(connectionString));
        if (executor is not null)
        {
            services.RemoveAll<IDatabaseMigrationExecutor>();
            services.AddScoped<IDatabaseMigrationExecutor>(_ => executor);
        }
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private async Task<StartupDatabaseGateResult> RunAsync(
        string target, string? allowCreate, IDatabaseMigrationExecutor? executor = null,
        CancellationToken token = default)
    {
        using var services = BuildServices(target, executor);
        var options = DocthecaStartupDatabase.CreateOptions(Config(allowCreate), target);
        var receipt = services.GetRequiredService<StartupDatabaseReceipt>();
        var result = await services.GetRequiredService<StartupDatabaseGate>().RunAsync(
            options, receipt, MigrationOrchestration.ServiceId, token);
        receipt.State.Should().Be(result.Succeeded
            ? ServiceMigrationReadinessState.Succeeded : ServiceMigrationReadinessState.Failed);
        receipt.ErrorCode.Should().Be(result.ErrorCode);
        if (result.ErrorCode is not null) result.ErrorCode.Should().NotContain(fixture.PasswordCanary);
        return result;
    }

    [Fact]
    public async Task ExistingTarget_NoMaintenancePrivilege_OnlyMigrationWrites()
    {
        // A login with no CREATEDB permission and no access to postgres must use the existing
        // target directly. Restore catalog privileges in finally, even when an assertion fails.
        var role = $"existing_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(fixture.GetConnectionString("postgres"));
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = $"CREATE ROLE {role} LOGIN PASSWORD 'fictitious-test-password'; ALTER DATABASE {database} OWNER TO {role}; REVOKE CONNECT ON DATABASE postgres FROM PUBLIC";
        await command.ExecuteNonQueryAsync();
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(fixture.GetConnectionString(database))
            { Username = role, Password = "fictitious-test-password" };
            (await RunAsync(connection.ConnectionString, "true")).Succeeded.Should().BeTrue();
            command.CommandText = "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = @name";
            command.Parameters.AddWithValue("@name", database);
            (await command.ExecuteScalarAsync()).Should().Be(role);
        }
        finally
        {
            command.Parameters.Clear();
            var owner = new NpgsqlConnectionStringBuilder(fixture.GetConnectionString(database)).Username;
            command.CommandText = $"GRANT CONNECT ON DATABASE postgres TO PUBLIC; ALTER DATABASE {database} OWNER TO {owner}";
            await command.ExecuteNonQueryAsync();
            // The migrated tables belong to this synthetic login and are removed by fixture cleanup.
        }
        using var context = fixture.CreateContext(database);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(MigrationGoldenStates.InitialId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingTarget_DefaultOrFalse_RefusesWithZeroWrites(string? value)
    {
        var missing = $"prep_{Guid.NewGuid():N}";
        var result = await RunAsync(fixture.GetConnectionString(missing), value);
        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("database_target_preparation.creation_not_allowed");
        result.ExecutorWasCalled.Should().BeFalse();
        (await DatabaseExistsAsync(missing)).Should().BeFalse();
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    public async Task MissingTarget_Allowed_CreatesMigratesAndSecondGateSkips(string value)
    {
        var missing = $"prep_{Guid.NewGuid():N}";
        try
        {
            var first = await RunAsync(fixture.GetConnectionString(missing), value);
            first.Succeeded.Should().BeTrue();
            first.ExecutorWasCalled.Should().BeTrue();
            using var context = fixture.CreateContext(missing);
            (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(MigrationGoldenStates.InitialId);
            var second = await RunAsync(fixture.GetConnectionString(missing), value);
            second.Succeeded.Should().BeTrue();
            second.ExecutorWasCalled.Should().BeFalse();
        }
        finally { await fixture.DropDatabaseAsync(missing); }
    }

    [Fact]
    public void InvalidAllowCreate_RejectsBeforeEvenParsingConnection_DoesNotEchoValue()
    {
        var canary = $"invalid-{Guid.NewGuid():N}";
        var exception = Record.Exception(() => DocthecaStartupDatabase.CreateOptions(Config(canary), "invalid connection"));
        exception.Should().BeOfType<InvalidOperationException>();
        exception!.Message.Should().Contain("Database:AllowCreate").And.Contain("database_target_preparation.invalid_target");
        exception.ToString().Should().NotContain(canary).And.NotContain("invalid connection");
    }

    [Fact]
    public async Task UnreachableServer_NoCreationFallback_SafeCode()
    {
        var target = new NpgsqlConnectionStringBuilder(fixture.GetConnectionString(database)) { Port = 1 }.ConnectionString;
        var result = await RunAsync(target, "true");
        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().StartWith("database_target_preparation.")
            .And.NotBe("database_target_preparation.creation_not_allowed");
        result.ExecutorWasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task AuthenticationFailure_NoCreationFallback_SafeCode()
    {
        var target = new NpgsqlConnectionStringBuilder(fixture.GetConnectionString(database))
        { Password = "fictitious-wrong-password" }.ConnectionString;
        var result = await RunAsync(target, "true");
        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().StartWith("database_target_preparation.");
        result.ExecutorWasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ConcurrentGates_CreateOneTarget_ExactlyOneMigrates()
    {
        var missing = $"prep_{Guid.NewGuid():N}";
        try
        {
            var runs = await Task.WhenAll(
                RunAsync(fixture.GetConnectionString(missing), "true"),
                RunAsync(fixture.GetConnectionString(missing), "true"));
            runs.Should().OnlyContain(r => r.Succeeded);
            runs.Count(r => r.ExecutorWasCalled).Should().Be(1);
            (await DatabaseExistsAsync(missing)).Should().BeTrue();
        }
        finally { await fixture.DropDatabaseAsync(missing); }
    }

    [Theory]
    [InlineData(true, "migration.execution_failed")]
    [InlineData(false, "migration.final_state_invalid")]
    public async Task MigrationOrFinalInspectionFailure_FailsReceipt_WithSafeCode(bool throwExecution, string expected)
    {
        var result = await RunAsync(fixture.GetConnectionString(database), null, new FailingExecutor(throwExecution, fixture.PasswordCanary));
        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(expected);
    }

    [Fact]
    public async Task Cancellation_PropagatesToken_NoSucceeded_AndGateCannotRunTwice()
    {
        var connection = fixture.GetConnectionString(database);
        using var services = BuildServices(connection);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var receipt = services.GetRequiredService<StartupDatabaseReceipt>();
        var gate = services.GetRequiredService<StartupDatabaseGate>();
        var options = DocthecaStartupDatabase.CreateOptions(Config(null), connection);
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            gate.RunAsync(options, receipt, MigrationOrchestration.ServiceId, cancelled.Token).AsTask());
        exception.CancellationToken.Should().Be(cancelled.Token);
        receipt.State.Should().Be(ServiceMigrationReadinessState.Running);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.RunAsync(options, receipt, MigrationOrchestration.ServiceId).AsTask());
    }

    private async Task<bool> DatabaseExistsAsync(string name)
    {
        await using var connection = new NpgsqlConnection(fixture.GetConnectionString("postgres"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name";
        command.Parameters.AddWithValue("@name", name);
        return await command.ExecuteScalarAsync() is not null;
    }

    private sealed class FailingExecutor(bool throwExecution, string canary) : IDatabaseMigrationExecutor
    {
        public ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(MigrationObservationState.Empty);
        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default) => throwExecution
            ? ValueTask.FromException(new InvalidOperationException(canary)) : ValueTask.CompletedTask;
    }
}
