using System.Data.Common;
using Doctheca.Database;
using Doctheca.Database.Repositories;
using Doctheca.Domain.Models.Structure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Doctheca.Tests.Database;

[Collection(MigrationIntegrationCollection.Name)]
public sealed class RequiredPolicyRepositoryTests(PostgreSqlMigrationFixture fixture) : IAsyncLifetime
{
    private string _database = null!;
    public async Task InitializeAsync() => _database = await fixture.CreateDatabaseAsync();
    public Task DisposeAsync() => fixture.DropDatabaseAsync(_database);

    [Fact]
    public async Task ConcurrentReplacements_LockBeforeTrackedRead_AndLaterCompleteSaveWins()
    {
        using var setup = fixture.CreateContext(_database);
        await new DocthecaMigrationExecutor(setup).ExecuteAsync();
        var initial = Definition();
        await new StructureDefinitionRepository(setup).AddAsync(initial);
        var firstBoundary = new ReadBoundary(pauseRead: true);
        var secondBoundary = new ReadBoundary(pauseRead: false);
        using var firstContext = Context(firstBoundary);
        using var secondContext = Context(secondBoundary);
        var first = Copy(initial, "First", true, true);
        var second = Copy(initial, "Second", false, false);
        var firstSave = new StructureDefinitionRepository(firstContext).UpdateAsync(first);
        await firstBoundary.ReadReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondSave = new StructureDefinitionRepository(secondContext).UpdateAsync(second);
        try
        {
            await secondBoundary.LockReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // PostgreSQL confirms the second connection is waiting on the first transaction.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!await setup.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid = {secondBoundary.BackendPid} AND wait_event_type = 'Lock') AS \"Value\"").SingleAsync(deadline.Token))
                await Task.Delay(20, deadline.Token);
            secondBoundary.ReadReached.Task.IsCompleted.Should().BeFalse("the definition lock precedes every tracked read");
        }
        finally { firstBoundary.Continue.TrySetResult(); }
        (await Task.WhenAll(firstSave, secondSave)).Should().OnlyContain(success => success);
        var saved = (await new StructureDefinitionRepository(setup).GetByIdAsync(initial.Id))!;
        saved.Name.Should().Be("Second");
        saved.Levels.Select(level => level.Required).Should().Equal(false, false);
        saved.Levels.Select(level => level.Id).Should().Equal(initial.Levels.Select(level => level.Id));
        saved.Levels[1].ParentLevelId.Should().Be(saved.Levels[0].Id);
        // Without the lock, the dirty-field union from [true,false] can be [false,true].
        (await setup.StructureLevels.AsNoTracking().OrderBy(level => level.SortOrder).Select(level => level.Required).ToListAsync())
            .Should().Equal(false, false);
    }

    [Fact]
    public async Task FailureBeforeCommit_RollsBackNameParentsLevelsAndFlags_AndMissingDefinitionStaysFalse()
    {
        using var setup = fixture.CreateContext(_database);
        await new DocthecaMigrationExecutor(setup).ExecuteAsync();
        var initial = Definition();
        await new StructureDefinitionRepository(setup).AddAsync(initial);
        using var attempt = new DocthecaDbContext(new DbContextOptionsBuilder<DocthecaDbContext>()
            .UseNpgsql(fixture.GetConnectionString(_database)).AddInterceptors(new FailCommit()).Options);
        var changed = Copy(initial, "Must roll back", false, false);
        changed.Levels.Add(new StructureLevelModel { Id = Guid.NewGuid(), DefinitionId = initial.Id, Name = "New", SortOrder = 2 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new StructureDefinitionRepository(attempt).UpdateAsync(changed));
        var saved = (await new StructureDefinitionRepository(setup).GetByIdAsync(initial.Id))!;
        saved.Should().BeEquivalentTo(initial);
        using var missing = fixture.CreateContext(_database);
        (await new StructureDefinitionRepository(missing).UpdateAsync(Definition())).Should().BeFalse();
    }

    private DocthecaDbContext Context(DbCommandInterceptor interceptor) => new(new DbContextOptionsBuilder<DocthecaDbContext>()
        .UseNpgsql(fixture.GetConnectionString(_database)).AddInterceptors(interceptor).Options);

    private static StructureDefinitionModel Definition()
    {
        var id = Guid.NewGuid();
        var root = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
        return new() { Id = id, Name = "Initial", CreatedAt = now, UpdatedAt = now, Levels = [
            new() { Id = root, DefinitionId = id, Name = "Root", Required = true, SortOrder = 0 },
            new() { Id = Guid.NewGuid(), DefinitionId = id, Name = "Child", ParentLevelId = root, SortOrder = 1 }
        ] };
    }

    private static StructureDefinitionModel Copy(StructureDefinitionModel initial, string name, bool root, bool child) => new()
    {
        Id = initial.Id, Name = name, CreatedAt = initial.CreatedAt, UpdatedAt = initial.UpdatedAt,
        Levels = initial.Levels.Select((level, i) => new StructureLevelModel {
            Id = level.Id, DefinitionId = initial.Id, Name = level.Name, ParentLevelId = level.ParentLevelId,
            SortOrder = i, Required = i == 0 ? root : child
        }).ToList()
    };

    private sealed class ReadBoundary(bool pauseRead) : DbCommandInterceptor
    {
        public TaskCompletionSource LockReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BackendPid { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
            {
                BackendPid = ((Npgsql.NpgsqlConnection)command.Connection!).ProcessID;
                LockReached.TrySetResult();
            }
            else if (command.CommandText.Contains("FROM structure_definitions", StringComparison.Ordinal))
            {
                LockReached.Task.IsCompleted.Should().BeTrue("the lock must precede the tracked entity query");
                ReadReached.TrySetResult();
                if (pauseRead) await Continue.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }

    private sealed class FailCommit : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Synthetic definition pre-commit failure");
    }
}
