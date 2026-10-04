using Doctheca.Database;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ServiceMantle.Bootstrap;
using ServiceMantle.Health;
using ServiceMantle.Migration;
using Xunit;

namespace Doctheca.Tests.Database;

[Collection(MigrationIntegrationCollection.Name)]
public sealed class StartupDatabaseHealthTests(PostgreSqlMigrationFixture fixture)
{
    [Theory]
    [InlineData(false, "doctheca.startup_incomplete")]
    [InlineData(true, "doctheca.startup_failed")]
    public async Task IncompleteReceipt_NoDatabaseAccess(bool failed, string expected)
    {
        using var services = DatabaseTargetPreparationTests.BuildServices("Host=synthetic.invalid;Database=no_io;Username=unused");
        var receipt = services.GetRequiredService<StartupDatabaseReceipt>();
        receipt.TryMarkRunning().Should().BeTrue();
        if (failed) receipt.TryCompleteFailed("migration.execution_failed").Should().BeTrue();
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IServiceHealthSnapshotSource>().GetSnapshotAsync();
        snapshot.ErrorCode.Should().Be(expected);
        snapshot.MigrationStatus.Should().Be(failed ? ServiceMigrationReadinessState.Failed : ServiceMigrationReadinessState.Running);
        context.Database.GetDbConnection().State.Should().Be(System.Data.ConnectionState.Closed);
    }

    [Fact]
    public async Task AuthenticationSqlState28_OpeningFailureRemainsDatabaseUnreachable()
    {
        var target = new NpgsqlConnectionStringBuilder(fixture.GetConnectionString("postgres"))
        { Password = "synthetic-wrong-password" }.ConnectionString;
        using var services = DatabaseTargetPreparationTests.BuildServices(target);
        var receipt = services.GetRequiredService<StartupDatabaseReceipt>();
        receipt.TryMarkRunning(); receipt.TryCompleteSucceeded();
        using var scope = services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<IServiceHealthSnapshotSource>();
        var snapshot = await source.GetSnapshotAsync();
        snapshot.MigrationStatus.Should().Be(ServiceMigrationReadinessState.Succeeded);
        snapshot.DatabaseStatus.Should().Be(ServiceDatabaseReadinessState.Unreachable);
        snapshot.ErrorCode.Should().Be("doctheca.database_unreachable");
        System.Text.Json.JsonSerializer.Serialize(snapshot).Should().NotContain("synthetic-wrong-password").And.NotContain(fixture.PasswordCanary);
    }

    [Fact]
    public async Task Classifier_OpenConnectionDelegatesSchemaAndUnknownFailures_CancellationPropagates()
    {
        using var services = DatabaseTargetPreparationTests.BuildServices(fixture.GetConnectionString("postgres"));
        var receipt = services.GetRequiredService<StartupDatabaseReceipt>();
        receipt.TryMarkRunning(); receipt.TryCompleteSucceeded();
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        await context.Database.OpenConnectionAsync();
        var classifier = scope.ServiceProvider.GetRequiredService<IServiceDatabaseProbeFailureClassifier>();
        classifier.Classify(new PostgresException("synthetic", "ERROR", "ERROR", "42P01"))
            .Should().Be(ServiceDatabaseProbeFailureKind.SchemaUnreadable);
        classifier.Classify(new InvalidOperationException("synthetic"))
            .Should().Be(ServiceDatabaseProbeFailureKind.Unclassified);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => scope.ServiceProvider
            .GetRequiredService<IServiceHealthSnapshotSource>().GetSnapshotAsync(cts.Token).AsTask());
        exception.CancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task MappedSchemaProbe_CancellationDuringBlockedQuery_PropagatesAndRecovers()
    {
        var database = await fixture.CreateDatabaseAsync();
        try
        {
            using (var seed = fixture.CreateContext(database)) await seed.Database.MigrateAsync();
            await using var blocker = new NpgsqlConnection(fixture.GetConnectionString(database));
            await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using var command = blocker.CreateCommand();
            command.CommandText = "LOCK TABLE document_files, document_parses, document_parse_images, document_parse_blocks IN ACCESS EXCLUSIVE MODE";
            await command.ExecuteNonQueryAsync();
            var target = new NpgsqlConnectionStringBuilder(fixture.GetConnectionString(database))
            { ApplicationName = "doctheca-cancelled-health-probe" }.ConnectionString;
            using var services = DatabaseTargetPreparationTests.BuildServices(target);
            var receipt = services.GetRequiredService<StartupDatabaseReceipt>();
            receipt.TryMarkRunning(); receipt.TryCompleteSucceeded();
            using var scope = services.CreateScope();
            using var cts = new CancellationTokenSource();
            var probe = scope.ServiceProvider.GetRequiredService<IServiceHealthSnapshotSource>()
                .GetSnapshotAsync(cts.Token).AsTask();
            // Observe the real blocked query before cancelling, rather than relying on a delay.
            await using var monitor = new NpgsqlConnection(fixture.GetConnectionString("postgres"));
            await monitor.OpenAsync();
            await using var monitorCommand = monitor.CreateCommand();
            monitorCommand.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE application_name = 'doctheca-cancelled-health-probe' AND wait_event_type = 'Lock'";
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                while ((long)(await monitorCommand.ExecuteScalarAsync(deadline.Token))! == 0)
                    await Task.Delay(20, deadline.Token);
            }
            finally { await cts.CancelAsync(); }
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
            exception.CancellationToken.Should().Be(cts.Token);
            receipt.State.Should().Be(ServiceMigrationReadinessState.Succeeded);
            await transaction.RollbackAsync();
            using var recoveredScope = services.CreateScope();
            var recovered = await recoveredScope.ServiceProvider.GetRequiredService<IServiceHealthSnapshotSource>().GetSnapshotAsync();
            recovered.DatabaseStatus.Should().Be(ServiceDatabaseReadinessState.Reachable);
            recovered.ErrorCode.Should().BeNull();
        }
        finally { await fixture.DropDatabaseAsync(database); }
    }

    [Fact]
    public async Task DeploymentIdentity_ExcludesCredentials_ObservesCancellation_AndCannotCollide()
    {
        using var services = DatabaseTargetPreparationTests.BuildServices(fixture.GetConnectionString("postgres"));
        var declaration = services.GetRequiredService<IDatabaseDeploymentCapabilityProvider>();
        var first = await declaration.GetCanonicalTargetIdentityAsync(
            MigrationOrchestration.Target("Host=EXAMPLE;Port=5432;Database=abc;Username=synthetic-user;Password=synthetic-password"), default);
        var second = await declaration.GetCanonicalTargetIdentityAsync(
            MigrationOrchestration.Target("Host=example;Port=5432;Database=abc;Username=other;Password=other"), default);
        first.Should().Be(second).And.NotContain("synthetic");
        var different = await declaration.GetCanonicalTargetIdentityAsync(
            MigrationOrchestration.Target("Host=example;Port=5432;Database=abc:def"), default);
        first.Should().NotBe(different);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            declaration.GetCanonicalTargetIdentityAsync(MigrationOrchestration.Target(fixture.GetConnectionString("postgres")), cts.Token).AsTask());
        exception.CancellationToken.Should().Be(cts.Token);
    }
}
