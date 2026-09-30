using System.Data.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Doctheca.Host;
using Xunit;

namespace Doctheca.Tests.Database;

/// <summary>
/// The deployment-validation and target-preparation stage (issue #53) against real PostgreSQL:
/// an existing database is used as-is with no maintenance connection or CREATE; a verifiably
/// missing database refuses startup with the fixed <c>DOCTHECA_DB_CREATION_NOT_ALLOWED</c> code
/// by default (and with an explicit <c>false</c>) while writing nothing;
/// <c>Database:AllowCreate=true</c> creates the missing target with the same credentials and
/// the migrated baseline follows; a non-boolean switch value fails startup fast without echoing
/// the value; an unreachable server is refused with the provider's safe code and never falls
/// back to creation; concurrent preparations converge on one created database; and the secret
/// canary never enters any log or exception surface.
/// </summary>
[Collection(MigrationIntegrationCollection.Name)]
public sealed class DatabaseTargetPreparationTests : IAsyncLifetime
{
    private readonly PostgreSqlMigrationFixture _fixture;
    private string _database = null!;

    public DatabaseTargetPreparationTests(PostgreSqlMigrationFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => _database = await _fixture.CreateDatabaseAsync();

    public async Task DisposeAsync() => await _fixture.DropDatabaseAsync(_database);

    private string MissingDatabase => $"prep_{Guid.NewGuid():N}";

    private static IConfiguration Config(string? allowCreate) =>
        new ConfigurationBuilder().AddInMemoryCollection(allowCreate is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { [DocthecaDatabaseTargetPreparer.AllowCreateConfigurationKey] = allowCreate })
        .Build();

    // ---------- existing target ----------

    [Fact]
    public async Task ExistingDatabase_IsUsedAsIs_NothingModified()
    {
        using var loggerProvider = new CapturingLoggerProvider();
        var logger = loggerProvider.CreateLogger("DocthecaDatabaseTargetPreparer");

        await DocthecaDatabaseTargetPreparer.PrepareAsync(
            Config(allowCreate: "true"),
            _fixture.GetConnectionString(_database),
            logger);

        var owner = await GetDatabaseOwnerAsync(_database);
        owner.Should().Be(Username(), "an existing target is never re-owned or modified");
        loggerProvider.Messages.Should().Contain(message =>
            message.Contains("using it as-is", StringComparison.Ordinal));
        loggerProvider.Messages.Should().NotContain(message =>
            message.Contains(_fixture.PasswordCanary, StringComparison.Ordinal));
    }

    // ---------- missing target: default refusal ----------

    [Fact]
    public async Task MissingDatabase_DefaultRefusesWithFixedCode_ZeroWrites()
    {
        var missing = MissingDatabase;

        var exception = await FluentActions.Awaiting(() => DocthecaDatabaseTargetPreparer.PrepareAsync(
                Config(allowCreate: null),
                _fixture.GetConnectionString(missing),
                NullLogger.Instance))
            .Should().ThrowAsync<DocthecaDatabaseTargetPreparationException>();

        exception.Which.ErrorCode.Should()
            .Be(DocthecaDatabaseTargetPreparer.CreationNotAllowedErrorCode);
        exception.Which.Message.Should().Contain("Database:AllowCreate");
        exception.Which.Message.Should().NotContain(_fixture.PasswordCanary);

        // Zero writes: neither the catalog nor any table appeared.
        (await DatabaseExistsAsync(missing)).Should().BeFalse();
    }

    [Fact]
    public async Task MissingDatabase_ExplicitFalse_RefusesIdentically()
    {
        var missing = MissingDatabase;

        var exception = await FluentActions.Awaiting(() => DocthecaDatabaseTargetPreparer.PrepareAsync(
                Config(allowCreate: "false"),
                _fixture.GetConnectionString(missing),
                NullLogger.Instance))
            .Should().ThrowAsync<DocthecaDatabaseTargetPreparationException>();

        exception.Which.ErrorCode.Should()
            .Be(DocthecaDatabaseTargetPreparer.CreationNotAllowedErrorCode);
        (await DatabaseExistsAsync(missing)).Should().BeFalse();
    }

    // ---------- missing target: allowed creation ----------

    [Fact]
    public async Task MissingDatabase_AllowCreateTrue_CreatesTarget_ThenExecutorMigrates()
    {
        var missing = MissingDatabase;

        await DocthecaDatabaseTargetPreparer.PrepareAsync(
            Config(allowCreate: "true"),
            _fixture.GetConnectionString(missing),
            NullLogger.Instance);

        (await DatabaseExistsAsync(missing)).Should().BeTrue();
        (await GetDatabaseOwnerAsync(missing)).Should().Be(Username());

        // The created target is immediately usable: the executor migrates it to the baseline.
        using var context = _fixture.CreateContext(missing);
        await new Doctheca.Database.DocthecaMigrationExecutor(context).ExecuteAsync();
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should()
            .Equal(MigrationGoldenStates.InitialId);

        // A second preparation over the now-existing target takes the as-is path.
        await DocthecaDatabaseTargetPreparer.PrepareAsync(
            Config(allowCreate: "true"),
            _fixture.GetConnectionString(missing),
            NullLogger.Instance);
    }

    // ---------- configuration validation ----------

    [Fact]
    public async Task InvalidAllowCreateValue_FailsFast_WithoutEchoingValue()
    {
        var missing = MissingDatabase;

        var exception = await FluentActions.Awaiting(() => DocthecaDatabaseTargetPreparer.PrepareAsync(
                Config(allowCreate: "maybe"),
                _fixture.GetConnectionString(missing),
                NullLogger.Instance))
            .Should().ThrowAsync<DocthecaDatabaseTargetPreparationException>();

        exception.Which.ErrorCode.Should()
            .Be(DocthecaDatabaseTargetPreparer.InvalidConfigurationErrorCode);
        exception.Which.Message.Should().NotContain("maybe",
            "the invalid value must not be echoed back");
        (await DatabaseExistsAsync(missing)).Should().BeFalse();
    }

    // ---------- refusal without creation fallback ----------

    [Fact]
    public async Task UnreachableServer_RefusedWithProviderCode_NoCreationFallback()
    {
        var canaryConnectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = 1,
            Database = "unreachable_target",
            Username = "postgres",
            Password = _fixture.PasswordCanary,
        }.ConnectionString;

        var exception = await FluentActions.Awaiting(() => DocthecaDatabaseTargetPreparer.PrepareAsync(
                Config(allowCreate: "true"),
                canaryConnectionString,
                NullLogger.Instance))
            .Should().ThrowAsync<DocthecaDatabaseTargetPreparationException>();

        // The provider's safe classification (database_target_preparation.*) — never the
        // creation refusal code and never a silent fallback.
        exception.Which.ErrorCode.Should().StartWith("database_target_preparation.");
        exception.Which.ErrorCode.Should().NotBe(DocthecaDatabaseTargetPreparer.CreationNotAllowedErrorCode);
        exception.Which.Message.Should().NotContain(_fixture.PasswordCanary);
    }

    // ---------- concurrent preparation ----------

    [Fact]
    public async Task ConcurrentPreparation_ConvergesOnOneCreatedTarget()
    {
        var missing = MissingDatabase;

        // Two instances validating the same missing target concurrently: creation is allowed,
        // the shared provider converges (Created / AlreadyExists), and neither instance is
        // refused with the creation-not-allowed code.
        await Task.WhenAll(
            Task.Run(() => DocthecaDatabaseTargetPreparer.PrepareAsync(
                Config(allowCreate: "true"),
                _fixture.GetConnectionString(missing),
                NullLogger.Instance)),
            Task.Run(() => DocthecaDatabaseTargetPreparer.PrepareAsync(
                Config(allowCreate: "true"),
                _fixture.GetConnectionString(missing),
                NullLogger.Instance)));

        (await DatabaseExistsAsync(missing)).Should().BeTrue();
    }

    // ---------- helpers ----------

    private string Username()
    {
        var connection = new DbConnectionStringBuilder
        {
            ConnectionString = _fixture.GetConnectionString("postgres"),
        };
        return (string)connection["Username"];
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

    private async Task<string> GetDatabaseOwnerAsync(string database)
    {
        await using var connection = new NpgsqlConnection(_fixture.GetConnectionString("postgres"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = @name";
        command.Parameters.AddWithValue("@name", database);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
