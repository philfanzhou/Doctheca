using System.Data.Common;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Database.Repositories;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services.Structure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Doctheca.Tests.Database;

[Collection(MigrationIntegrationCollection.Name)]
public sealed class DocumentAuthoringConcurrencyTests(PostgreSqlMigrationFixture fixture) : IAsyncLifetime
{
    private string _database = null!;
    public async Task InitializeAsync() { _database = await fixture.CreateDatabaseAsync(); using var db = fixture.CreateContext(_database); await db.Database.EnsureCreatedAsync(); }
    public Task DisposeAsync() => fixture.DropDatabaseAsync(_database);
    private DocthecaDbContext Context(IInterceptor? barrier = null)
    {
        var options = new DbContextOptionsBuilder<DocthecaDbContext>().UseNpgsql(fixture.GetConnectionString(_database));
        if (barrier is not null) options.AddInterceptors(barrier);
        return new(options.Options);
    }
    private async Task<(Guid File, Guid Level, Guid Parse, Guid Node, Guid Audio)> Seed()
    {
        using var db = Context(); var definition = new StructureDefinitionEntity { Name = "Arbitrary" }; db.StructureDefinitions.Add(definition);
        var level = new StructureLevelEntity { DefinitionId = definition.Id, Name = "Top" }; db.StructureLevels.Add(level); await db.SaveChangesAsync();
        var repo = new DocumentAuthoringRepository(db); var created = await repo.CreateAsync(new("Book", null, null, null, true, definition.Id), null, default);
        var file = created.Value!.Id; await repo.AttachSourceAsync(file, Guid.NewGuid(), "application/pdf", default);
        var parse = new DocumentParseEntity { DocumentFileId = file, Status = DocumentParseStatus.Parsed }; db.DocumentParses.Add(parse);
        db.DocumentParseBlocks.Add(new() { ParseId = parse.Id, Sequence = 0, BlockType = "text", TextContent = "old text", BlockData = "{}" }); await db.SaveChangesAsync();
        var tree = (await repo.SaveTreeAsync(file, [Node(level.Id, "Chapter")], default)).Value!.Tree;
        var node = tree.Nodes.Single().Id;
        var audio = new DocumentStructureAudioEntity { DocumentFileId = file, TreeId = tree.TreeId!.Value, NodeId = node, SourcePath = "track.wav", ObjectKey = "audio/preserved.wav", ContentType = "audio/wav", SizeBytes = 12 };
        db.DocumentStructureAudio.Add(audio); await db.SaveChangesAsync();
        return (file, level.Id, parse.Id, node, audio.Id);
    }
    private static DocumentNodeInput Node(Guid level, string name, Guid? id = null, Guid? parse = null) => new(id, level, name, parse, parse.HasValue ? 0 : null, parse.HasValue ? 0 : null, []);

    [Theory]
    [InlineData("second-save")] [InlineData("definition")] [InlineData("delete-parse")]
    public async Task WaitingSave_ValidatesCommittedOwnerDefinitionAndBinding(string action)
    {
        var seed = await Seed(); using var setup = Context(); var barrier = new OwnerBarrier(); using var delayed = Context(barrier);
        var secondId = Guid.NewGuid();
        DocumentNodeInput[] late = action == "second-save" ? [Node(seed.Level, "Late", seed.Node, seed.Parse), Node(seed.Level, "Late second", secondId)] : [Node(seed.Level, "Late", seed.Node, seed.Parse)];
        var pending = new DocumentAuthoringRepository(delayed).SaveTreeAsync(seed.File, late, default);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (action == "second-save") (await new DocumentAuthoringRepository(setup).SaveTreeAsync(seed.File, [Node(seed.Level, "Earlier", seed.Node), Node(seed.Level, "Earlier two"), Node(seed.Level, "Earlier three")], default)).Status.Should().Be(StructureResultStatus.Ok);
            if (action == "definition")
            {
                // Clear the tree first so a definition replacement is valid while the old save waits.
                await new DocumentAuthoringRepository(setup).SaveTreeAsync(seed.File, [], default);
                (await new DocumentAuthoringRepository(setup).UpdateAsync(seed.File, new(null, "Manual", null, null, true, null), default)).Status.Should().Be(StructureResultStatus.Ok);
            }
            if (action == "delete-parse") await new DocumentParseRepository(setup).DeleteAsync(seed.Parse);
        }
        finally { barrier.Resume.TrySetResult(); }
        var result = await pending;
        result.Status.Should().Be(action == "second-save" ? StructureResultStatus.Ok : StructureResultStatus.Invalid);
        var saved = await new DocumentAuthoringRepository(setup).GetTreeAsync(seed.File, default);
        saved!.Nodes.Select(n => n.Name).Should().Equal(action == "definition" ? [] : action == "second-save" ? ["Late", "Late second"] : ["Chapter"]);
        var audio = await setup.DocumentStructureAudio.AsNoTracking().SingleAsync(a => a.Id == seed.Audio);
        audio.NodeId.Should().Be(action == "definition" ? null : seed.Node); audio.ObjectKey.Should().Be("audio/preserved.wav");
    }

    [Fact]
    public async Task WaitingDefinitionChange_CannotPartiallyWriteMetadataAfterNewTreeSave()
    {
        var seed = await Seed(); using var setup = Context(); await new DocumentAuthoringRepository(setup).SaveTreeAsync(seed.File, [], default);
        var barrier = new OwnerBarrier(); using var delayed = Context(barrier);
        var pending = new DocumentAuthoringRepository(delayed).UpdateAsync(seed.File, new("Wrong", "Wrong", null, null, true, null), default);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await new DocumentAuthoringRepository(setup).SaveTreeAsync(seed.File, [Node(seed.Level, "Committed")], default); }
        finally { barrier.Resume.TrySetResult(); }
        (await pending).Status.Should().Be(StructureResultStatus.Conflict);
        var file = await setup.DocumentFiles.AsNoTracking().SingleAsync(); file.FileName.Should().Be("Book"); file.Subject.Should().BeNull();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task WaitingCompletion_OnlyDetachesOldBindingAndKeepsNewerExplicitBinding(bool newBinding)
    {
        var seed = await Seed(); using var setup = Context(); var repo = new DocumentAuthoringRepository(setup);
        var before = (await repo.SaveTreeAsync(seed.File, [Node(seed.Level, "Chapter", seed.Node, seed.Parse)], default)).Value!.Tree;
        var job = await new DocumentParseRepository(setup).AddAsync(new() { DocumentFileId = seed.File });
        var barrier = new OwnerBarrier(); using var delayed = Context(barrier);
        var pending = new DocumentParseRepository(delayed).CompleteAsync(job.Id, Guid.NewGuid(), "new", [], [], []);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { if (newBinding) await repo.SaveTreeAsync(seed.File, [Node(seed.Level, "Renamed", seed.Node, seed.Parse)], default); }
        finally { barrier.Resume.TrySetResult(); }
        await pending;
        var tree = (await repo.GetTreeAsync(seed.File, default))!; tree.TreeId.Should().Be(before.TreeId);
        var node = tree.Nodes.Single(); node.Id.Should().Be(seed.Node); node.CreatedAt.Should().Be(before.Nodes.Single().CreatedAt); node.SortOrder.Should().Be(0);
        node.ParseId.Should().Be(newBinding ? seed.Parse : null); node.Name.Should().Be(newBinding ? "Renamed" : "Chapter");
        (await setup.DocumentStructureAudio.AsNoTracking().SingleAsync()).NodeId.Should().Be(seed.Node);
    }

    [Fact]
    public async Task WaitingMissingFill_RechecksCurrentHumanFieldsAndInvalidMetadataIsAtomic()
    {
        var seed = await Seed(); var barrier = new OwnerBarrier(); using var delayed = Context(barrier); using var setup = Context();
        var pending = new DocumentAuthoringRepository(delayed).FillMissingAsync(seed.File, "LLM subject", "LLM grade", "2025", default);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { (await new DocumentAuthoringRepository(setup).UpdateAsync(seed.File, new("Manual title", "English", "9", null), default)).Status.Should().Be(StructureResultStatus.Ok); }
        finally { barrier.Resume.TrySetResult(); }
        var updated = (await pending)!; updated.FileName.Should().Be("Manual title"); updated.Subject.Should().Be("English"); updated.Grade.Should().Be("9"); updated.Year.Should().Be("2025");
        var invalid = await new DocumentAuthoringRepository(setup).UpdateAsync(seed.File, new("Wrong", new string('x', 51), "wrong", null), default);
        invalid.Status.Should().Be(StructureResultStatus.Invalid);
        var file = await setup.DocumentFiles.AsNoTracking().SingleAsync(); file.FileName.Should().Be("Manual title"); file.Grade.Should().Be("9");
    }

    [Theory]
    [InlineData("attach")] [InlineData("delete")] [InlineData("parse")]
    public async Task WaitingAttachmentOrEnqueue_RechecksSourceAtOwnerBoundary(string action)
    {
        var seed = await Seed(); using var setup = Context();
        await setup.DocumentFiles.ExecuteUpdateAsync(s => s.SetProperty(f => f.StructaDocDocumentId, (Guid?)null));
        var barrier = new OwnerBarrier(); using var delayed = Context(barrier); var source = Guid.NewGuid();
        Task pending = action == "parse" ? new DocumentParseRepository(delayed).AddAsync(new() { DocumentFileId = seed.File })
            : new DocumentAuthoringRepository(delayed).AttachSourceAsync(seed.File, source, "application/pdf", default);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var winner = Guid.NewGuid();
        try
        {
            if (action == "delete") await new DocumentFileRepository(setup).DeleteAsync(seed.File);
            else await new DocumentAuthoringRepository(setup).AttachSourceAsync(seed.File, winner, "application/pdf", default);
        }
        finally { barrier.Resume.TrySetResult(); }
        await pending;
        if (action == "parse") (await setup.DocumentParses.CountAsync()).Should().Be(2);
        else (await (Task<StructureResult<DocumentFileModel>>)pending).Status.Should().Be(action == "delete" ? StructureResultStatus.NotFound : StructureResultStatus.Conflict);
        if (action != "delete") (await setup.DocumentFiles.AsNoTracking().SingleAsync()).StructaDocDocumentId.Should().Be(winner);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SourceCommitFailureOrCancellation_RollsBackMetadataAndRetainsTreeAudio(bool cancel)
    {
        var seed = await Seed(); using var setup = Context(); await setup.DocumentFiles.ExecuteUpdateAsync(s => s.SetProperty(f => f.StructaDocDocumentId, (Guid?)null));
        using var cancellation = new CancellationTokenSource(); using var failed = Context(new AbortUpdate(cancellation, cancel));
        var error = await Record.ExceptionAsync(() => new DocumentAuthoringRepository(failed).AttachSourceAsync(seed.File, Guid.NewGuid(), "application/pdf", cancellation.Token));
        error.Should().NotBeNull();
        (await setup.DocumentFiles.AsNoTracking().SingleAsync()).StructaDocDocumentId.Should().BeNull();
        (await setup.DocumentStructureNodes.AsNoTracking().SingleAsync()).Id.Should().Be(seed.Node);
        (await setup.DocumentStructureAudio.AsNoTracking().SingleAsync()).NodeId.Should().Be(seed.Node);
    }
    private sealed class OwnerBarrier : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { if (command.CommandText.Contains("document_files") && command.CommandText.Contains("FOR UPDATE")) { Entered.TrySetResult(); await Resume.Task.WaitAsync(ct); } return result; }
    }
    private sealed class AbortUpdate(CancellationTokenSource cancellation, bool cancel) : DbCommandInterceptor
    {
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken ct = default)
        { if (command.CommandText.Contains("UPDATE document_files")) { if (cancel) cancellation.Cancel(); else throw new InvalidOperationException("Synthetic source failure"); } return ValueTask.FromResult(result); }
    }
}
