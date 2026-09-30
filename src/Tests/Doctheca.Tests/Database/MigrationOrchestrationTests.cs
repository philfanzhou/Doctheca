using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Doctheca.Database;
using ServiceMantle.Migration;
using Xunit;

namespace Doctheca.Tests.Database;

/// <summary>
/// The real-lock orchestration behavior of the startup migration chain (issue #53) against real
/// PostgreSQL: two concurrent instances over an empty database produce exactly one execution
/// (the second waits for the advisory lock, re-reads CurrentVersionCompatible, and skips with
/// <c>ExecutorWasCalled=false</c>); an already-migrated database skips execution entirely; a
/// verified legacy database is orchestrated through PendingMigration to the stamped baseline; a
/// too-new history and an undecidable structure refuse with the shared orchestrator's safe
/// codes and zero writes; a held lock times out safely and the next orchestration recovers;
/// caller cancellation while waiting for the lock propagates on the caller's own token;
/// controlled executor failures, a final-state mismatch, and a lost lease during execution
/// never report success and always release the lock for the next session; and the secret canary
/// never enters any result surface.
/// </summary>
[Collection(MigrationIntegrationCollection.Name)]
public sealed class MigrationOrchestrationTests : IAsyncLifetime
{
    private readonly PostgreSqlMigrationFixture _fixture;
    private string _database = null!;

    public MigrationOrchestrationTests(PostgreSqlMigrationFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => _database = await _fixture.CreateDatabaseAsync();

    public async Task DisposeAsync() => await _fixture.DropDatabaseAsync(_database);

    private string ConnectionString => _fixture.GetConnectionString(_database);

    // ---------- multi-instance serialization ----------

    [Fact]
    public async Task TwoConcurrentInstances_EmptyDatabase_ExactlyOneExecutes_OtherWaitsAndSkips()
    {
        using var context1 = _fixture.CreateContext(_database);
        using var context2 = _fixture.CreateContext(_database);
        var orchestrator1 = MigrationOrchestration.CreateOrchestrator(
            new DocthecaMigrationExecutor(context1, NullLogger.Instance));
        var orchestrator2 = MigrationOrchestration.CreateOrchestrator(
            new DocthecaMigrationExecutor(context2, NullLogger.Instance));
        using var barrier = new Barrier(2);

        var runs = await Task.WhenAll(
            Task.Run(async () =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(30));
                return await orchestrator1.OrchestrateMigrationAsync(
                    MigrationOrchestration.ServiceId,
                    MigrationOrchestration.Target(ConnectionString),
                    MigrationOrchestration.DefaultAcquireTimeout).AsTask();
            }),
            Task.Run(async () =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(30));
                return await orchestrator2.OrchestrateMigrationAsync(
                    MigrationOrchestration.ServiceId,
                    MigrationOrchestration.Target(ConnectionString),
                    MigrationOrchestration.DefaultAcquireTimeout).AsTask();
            }));

        // Both instances may start; only the lock holder executes, the other waits for the
        // advisory lock, re-reads the state, and skips.
        runs.Should().OnlyContain(result => result.Succeeded);
        runs.Count(result => result.ExecutorWasCalled).Should().Be(1);
        runs.Count(result => !result.ExecutorWasCalled).Should().Be(1);

        // The four business tables and exactly the baseline history row exist once.
        using var verify = _fixture.CreateContext(_database);
        foreach (var table in DocthecaMigrationExecutor.KnownTableNames)
        {
            (await MigrationGoldenStates.CountRowsAsync(verify, table)).Should().Be(0,
                $"table '{table}' must exist after the concurrent startup");
        }

        (await MigrationGoldenStates.ReadAppliedHistoryAsync(verify)).Should()
            .Equal(MigrationGoldenStates.InitialId);
    }

    // ---------- inspection-driven skipping and takeover ----------

    [Fact]
    public async Task AlreadyMigratedDatabase_OrchestrationSkipsExecution()
    {
        using (var seed = _fixture.CreateContext(_database))
        {
            await new DocthecaMigrationExecutor(seed, NullLogger.Instance).ExecuteAsync();
        }

        using var context = _fixture.CreateContext(_database);
        var result = await MigrationOrchestration.OrchestrateAsync(context, ConnectionString);

        result.Succeeded.Should().BeTrue();
        result.ExecutorWasCalled.Should().BeFalse(
            "a CurrentVersionCompatible target must skip execution and release the lock");
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task VerifiedLegacyDatabase_OrchestratesPendingMigrationToStampedBaseline()
    {
        // The legacy state: all four tables without history, plus business rows to preserve.
        using (var seed = _fixture.CreateContext(_database))
        {
            await MigrationGoldenStates.EnsureCreatedLegacyAsync(seed);
            await MigrationGoldenStates.ExecuteRawAsync(seed, MigrationGoldenStates.TriggerSql);
            await MigrationGoldenStates.SeedBusinessRowsAsync(seed);
        }

        using var context = _fixture.CreateContext(_database);
        var result = await MigrationOrchestration.OrchestrateAsync(context, ConnectionString);

        // LegacyTakeoverRequired maps to PendingMigration: the orchestrator executes the
        // executor exactly once and the held-lock final inspection must see Current again.
        result.Succeeded.Should().BeTrue();
        result.ExecutorWasCalled.Should().BeTrue();

        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should()
            .Equal(MigrationGoldenStates.InitialId);
        (await MigrationGoldenStates.CountRowsAsync(context, "document_files")).Should().Be(1);
        (await MigrationGoldenStates.CountRowsAsync(context, "document_parse_blocks")).Should().Be(1);
    }

    // ---------- refused states (zero writes) ----------

    [Fact]
    public async Task VersionTooNewHistory_RefusedSafely_WithoutExecuting()
    {
        using (var seed = _fixture.CreateContext(_database))
        {
            await MigrationGoldenStates.ExecuteRawAsync(seed, $$"""
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" character varying(150) NOT NULL PRIMARY KEY,
                    "ProductVersion" character varying(32) NOT NULL
                )
                """);
            await MigrationGoldenStates.ExecuteRawAsync(seed, """
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('99991231235959_Future', '10.0.12')
                """);
        }

        using var context = _fixture.CreateContext(_database);
        var result = await MigrationOrchestration.OrchestrateAsync(context, ConnectionString);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(WellKnownMigrationErrorCodes.VersionTooNew);
        result.ExecutorWasCalled.Should().BeFalse();

        // Refusal is a zero-write outcome: no business table appeared.
        (await MigrationGoldenStates.ScalarAsync<long>(
            context,
            "SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tablename = 'document_files'"))
            .Should().Be(0);
        result.ErrorMessage.Should().NotContain(_fixture.PasswordCanary);
    }

    [Fact]
    public async Task UndecidableStructure_RefusedSafely_WithoutExecuting()
    {
        // A partial table set without history is an unknown structure: InspectionFailed.
        using (var seed = _fixture.CreateContext(_database))
        {
            await MigrationGoldenStates.ExecuteRawAsync(seed,
                "CREATE TABLE document_files (id uuid PRIMARY KEY)");
        }

        using var context = _fixture.CreateContext(_database);
        var result = await MigrationOrchestration.OrchestrateAsync(context, ConnectionString);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(WellKnownMigrationErrorCodes.InspectionFailed);
        result.ExecutorWasCalled.Should().BeFalse();

        // Zero writes: the public schema still contains exactly the one table the test seeded
        // (no history table, no completed business table, no repair attempt).
        (await MigrationGoldenStates.ScalarAsync<long>(
            context,
            "SELECT count(*) FROM pg_tables WHERE schemaname = 'public'"))
            .Should().Be(1, "an undecidable structure must not be written to");
        result.ErrorMessage.Should().NotContain(_fixture.PasswordCanary);
    }

    // ---------- lock acquisition boundaries ----------

    [Fact]
    public async Task LockHeldByAnotherSession_AcquireTimesOutSafely_AndRecoversAfterRelease()
    {
        var provider = new ServiceMantle.Database.PostgreSql.Migration.PostgreSqlMigrationLockProvider();
        var held = await provider.AcquireAsync(
            MigrationOrchestration.ServiceId,
            MigrationOrchestration.Target(ConnectionString),
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
        try
        {
            using var context = _fixture.CreateContext(_database);

            var result = await MigrationOrchestration.OrchestrateAsync(
                context, ConnectionString, acquireTimeout: TimeSpan.FromSeconds(2));

            // The 30-second budget is a deployment contract; a shorter bound proves the same
            // safe classification: fail closed with the stable lock-timeout code, executor
            // never called, and no degraded lock-free execution.
            result.Succeeded.Should().BeFalse();
            result.ErrorCode.Should().Be(WellKnownMigrationErrorCodes.LockTimeout);
            result.ExecutorWasCalled.Should().BeFalse();
            result.ErrorMessage.Should().NotContain(_fixture.PasswordCanary);
        }
        finally
        {
            await held.DisposeAsync();
        }

        using var recovery = _fixture.CreateContext(_database);
        var recovered = await MigrationOrchestration.OrchestrateAsync(recovery, ConnectionString);
        recovered.Succeeded.Should().BeTrue();
        recovered.ExecutorWasCalled.Should().BeTrue();
    }

    [Fact]
    public async Task CancelledWhileWaitingForLock_PropagatesOwnToken_ReleasedLockStaysAcquirable()
    {
        var provider = new ServiceMantle.Database.PostgreSql.Migration.PostgreSqlMigrationLockProvider();
        var held = await provider.AcquireAsync(
            MigrationOrchestration.ServiceId,
            MigrationOrchestration.Target(ConnectionString),
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
        try
        {
            using var context = _fixture.CreateContext(_database);
            using var cts = new CancellationTokenSource();

            var orchestrate = MigrationOrchestration.OrchestrateAsync(
                context, ConnectionString, cancellationToken: cts.Token);

            // Observe the real server state instead of guessing timing: the held lock occupies
            // one session and the orchestration's polling acquisition opens a second one on the
            // same database, so the waiter is provably inside lock acquisition.
            await WaitForSecondDatabaseSessionAsync();
            await cts.CancelAsync();

            var assertion = await FluentActions.Awaiting(() => orchestrate).Should()
                .ThrowAsync<OperationCanceledException>();
            assertion.Which.CancellationToken.Should().Be(cts.Token,
                "caller cancellation must propagate on the caller's own token");
        }
        finally
        {
            await held.DisposeAsync();
        }

        // After the cancellation and the release, another session acquires the same lock and
        // completes the migration.
        using var recovery = _fixture.CreateContext(_database);
        var recovered = await MigrationOrchestration.OrchestrateAsync(recovery, ConnectionString);
        recovered.Succeeded.Should().BeTrue();
        recovered.ExecutorWasCalled.Should().BeTrue();
    }

    // ---------- fail-closed orchestration outcomes ----------

    [Fact]
    public async Task ExecutorFailure_NeverSucceeds_LockReleasedForNextSession()
    {
        var scripted = new ScriptedExecutor(
            inspections: [MigrationObservationState.Empty],
            executeFailure: new InvalidOperationException("controlled executor failure"));
        var failed = await MigrationOrchestration.CreateOrchestrator(scripted)
            .OrchestrateMigrationAsync(
                MigrationOrchestration.ServiceId,
                MigrationOrchestration.Target(ConnectionString),
                MigrationOrchestration.DefaultAcquireTimeout)
            .AsTask();

        failed.Succeeded.Should().BeFalse();
        failed.ErrorCode.Should().Be(WellKnownMigrationErrorCodes.ExecutionFailed);
        failed.ExecutorWasCalled.Should().BeTrue();

        // The lock was released by the failing orchestration: the next real session acquires
        // the same lock and completes the migration.
        using var context = _fixture.CreateContext(_database);
        var recovered = await MigrationOrchestration.OrchestrateAsync(context, ConnectionString);
        recovered.Succeeded.Should().BeTrue();
        recovered.ExecutorWasCalled.Should().BeTrue();
    }

    [Fact]
    public async Task FinalStateMismatch_NeverSucceeds_LockReleasedForNextSession()
    {
        // Empty first, execute "succeeds", but the final inspection no longer reports current:
        // startup must refuse even though the executor believed it finished.
        var scripted = new ScriptedExecutor(inspections:
            [MigrationObservationState.Empty, MigrationObservationState.PendingMigration]);
        var mismatch = await MigrationOrchestration.CreateOrchestrator(scripted)
            .OrchestrateMigrationAsync(
                MigrationOrchestration.ServiceId,
                MigrationOrchestration.Target(ConnectionString),
                MigrationOrchestration.DefaultAcquireTimeout)
            .AsTask();

        mismatch.Succeeded.Should().BeFalse();
        mismatch.ErrorCode.Should().Be(WellKnownMigrationErrorCodes.FinalStateInvalid);
        mismatch.ExecutorWasCalled.Should().BeTrue();

        using var context = _fixture.CreateContext(_database);
        var recovered = await MigrationOrchestration.OrchestrateAsync(context, ConnectionString);
        recovered.Succeeded.Should().BeTrue();
        recovered.ExecutorWasCalled.Should().BeTrue();
    }

    [Fact]
    public async Task LeaseLostDuringExecution_NeverSucceeds_OtherSessionCanAcquire()
    {
        var scripted = new HangingExecutor();
        var orchestrate = MigrationOrchestration.CreateOrchestrator(scripted)
            .OrchestrateMigrationAsync(
                MigrationOrchestration.ServiceId,
                MigrationOrchestration.Target(ConnectionString),
                MigrationOrchestration.DefaultAcquireTimeout)
            .AsTask();

        // Barrier: the executor is inside ExecuteAsync under the held lock before we act.
        await scripted.EnteredExecute.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await TerminateAdvisoryLockHolderAsync();

        var lost = await orchestrate;
        lost.Succeeded.Should().BeFalse();
        lost.ErrorCode.Should().Be(WellKnownMigrationErrorCodes.LockFailed);
        lost.ExecutorWasCalled.Should().BeTrue();

        // The terminated session released the advisory lock: the next session acquires the
        // same lock and completes the migration.
        using var context = _fixture.CreateContext(_database);
        var recovered = await MigrationOrchestration.OrchestrateAsync(context, ConnectionString);
        recovered.Succeeded.Should().BeTrue();
        recovered.ExecutorWasCalled.Should().BeTrue();
    }

    [Fact]
    public async Task CallerCancellationDuringExecution_ObservesCallerToken_NotAFakedOutcome()
    {
        // The cancellation/lease-loss race resolves with the caller first: the orchestration
        // ends with the caller's own cancellation, never a synthesized success or failure.
        using var cts = new CancellationTokenSource();
        var scripted = new CancellingExecutor(cts);
        var orchestrate = MigrationOrchestration.CreateOrchestrator(scripted)
            .OrchestrateMigrationAsync(
                MigrationOrchestration.ServiceId,
                MigrationOrchestration.Target(ConnectionString),
                MigrationOrchestration.DefaultAcquireTimeout,
                cts.Token)
            .AsTask();

        await scripted.EnteredExecute.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var assertion = await FluentActions.Awaiting(() => orchestrate).Should()
            .ThrowAsync<OperationCanceledException>();
        assertion.Which.CancellationToken.Should().Be(cts.Token);

        // The next run over the still-empty database completes normally.
        using var context = _fixture.CreateContext(_database);
        var recovered = await MigrationOrchestration.OrchestrateAsync(context, ConnectionString);
        recovered.Succeeded.Should().BeTrue();
    }

    // ---------- executor interface mapping ----------

    [Fact]
    public async Task ExecutorInterface_MapsDatabaseStatesToObservationStates()
    {
        IDatabaseMigrationExecutor AsInterface(DocthecaDbContext context) =>
            new DocthecaMigrationExecutor(context, NullLogger.Instance);

        using (var empty = _fixture.CreateContext(_database))
        {
            (await AsInterface(empty).InspectAsync())
                .Should().Be(MigrationObservationState.Empty);
        }

        using (var seed = _fixture.CreateContext(_database))
        {
            await MigrationGoldenStates.EnsureCreatedLegacyAsync(seed);
            await MigrationGoldenStates.ExecuteRawAsync(seed, MigrationGoldenStates.TriggerSql);
        }

        using var legacy = _fixture.CreateContext(_database);
        // The verified legacy takeover state is the orchestrator's PendingMigration signal.
        (await AsInterface(legacy).InspectAsync())
            .Should().Be(MigrationObservationState.PendingMigration);
        await AsInterface(legacy).ExecuteAsync();

        using var current = _fixture.CreateContext(_database);
        (await AsInterface(current).InspectAsync())
            .Should().Be(MigrationObservationState.CurrentVersionCompatible);
    }

    // ---------- server-state observation helpers ----------

    private async Task WaitForSecondDatabaseSessionAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                if (await CountDatabaseSessionsAsync() >= 2)
                {
                    return;
                }

                await Task.Delay(50, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }

        throw new TimeoutException("The lock-acquisition session did not appear in pg_stat_activity.");
    }

    private async Task<int> CountDatabaseSessionsAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.GetConnectionString("postgres"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*)
            FROM pg_stat_activity
            WHERE datname = @database
            """;
        command.Parameters.AddWithValue("@database", _database);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private async Task TerminateAdvisoryLockHolderAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.GetConnectionString("postgres"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pg_terminate_backend(l.pid)
            FROM pg_locks l
            WHERE l.locktype = 'advisory' AND l.granted
              AND l.database = (SELECT oid FROM pg_database WHERE datname = @database)
            """;
        command.Parameters.AddWithValue("@database", _database);
        await command.ExecuteNonQueryAsync();
    }

    // ---------- controlled executors ----------

    /// <summary>
    /// An executor whose inspection sequence and execution outcome are scripted by the test; it
    /// never touches the database itself (the lock provider still uses a real connection).
    /// </summary>
    private sealed class ScriptedExecutor(
        IReadOnlyList<MigrationObservationState> inspections,
        Exception? executeFailure = null) : IDatabaseMigrationExecutor
    {
        private readonly Queue<MigrationObservationState> _inspections = new(inspections);

        public ValueTask<MigrationObservationState> InspectAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_inspections.Count > 0
                ? _inspections.Dequeue()
                : throw new InvalidOperationException("No scripted inspection left."));

        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default) =>
            executeFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(executeFailure);
    }

    /// <summary>
    /// Blocks inside ExecuteAsync until the authority token is cancelled, so a test can kill the
    /// lease-holding session while the executor cooperatively observes the loss.
    /// </summary>
    private sealed class HangingExecutor : IDatabaseMigrationExecutor
    {
        public TaskCompletionSource EnteredExecute { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<MigrationObservationState> InspectAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(MigrationObservationState.Empty);

        public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            EnteredExecute.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    /// <summary>
    /// Cancels the caller's token from inside ExecuteAsync and then cooperatively stops, which
    /// makes the caller's cancellation win the race against any lease or failure classification.
    /// </summary>
    private sealed class CancellingExecutor(CancellationTokenSource caller) : IDatabaseMigrationExecutor
    {
        public TaskCompletionSource EnteredExecute { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<MigrationObservationState> InspectAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(MigrationObservationState.Empty);

        public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            EnteredExecute.TrySetResult();
            await caller.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
