using System.Data.Common;
using System.Text.Json;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Database.Repositories;
using Doctheca.Domain.Models;
using Doctheca.Domain.Models.Structure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Doctheca.Tests.Database;

[Collection(MigrationIntegrationCollection.Name)]
public sealed class DocumentOwnedStructureTests(PostgreSqlMigrationFixture fixture) : IAsyncLifetime
{
    private string _database = null!;
    private const string Previous = DocthecaMigrationExecutor.AddRequiredStructureLevelsMigrationId;
    private const string Current = DocthecaMigrationExecutor.AddDocumentOwnedStructureMigrationId;
    public async Task InitializeAsync() => _database = await fixture.CreateDatabaseAsync();
    public Task DisposeAsync() => fixture.DropDatabaseAsync(_database);

    [Fact]
    public async Task PopulatedUpgrade_PreservesEveryLegacyValueAndHistoricalSet_AndSelectsCurrentOnce()
    {
        using var context = fixture.CreateContext(_database);
        await context.GetService<IMigrator>().MigrateAsync(Previous);
        await PageMigrationTests.SeedAllEarlierTablesAsync(context);
        await context.Database.ExecuteSqlAsync($"UPDATE document_parses SET status='parsed',parsed_at=NULL");
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            await context.Database.ExecuteSqlAsync($"""
                INSERT INTO document_parses (id,document_file_id,model_version,status,parsed_at)
                VALUES ({ids[i]},{MigrationGoldenStates.FileId},'vlm','parsed',{(i == 3 ? (DateTimeOffset?)null : DateTimeOffset.Parse("2026-10-01T00:00:00Z"))});
                INSERT INTO document_structure_audio (id,parse_id,node_id,source_path,content_type,size_bytes,object_key,match_status,created_at,updated_at)
                SELECT {Guid.NewGuid()},{ids[i]},NULL,source_path,content_type,size_bytes,{"audio/preserved/" + ids[i] + ".wav"},match_status,created_at,updated_at
                FROM document_structure_audio WHERE parse_id={MigrationGoldenStates.ParseId};
                """);
            if (i != 2) // Audio-only history is preserved but does not qualify as current.
                await context.Database.ExecuteSqlAsync($"""
                    INSERT INTO document_structure_nodes (id,parse_id,level_id,name,sort_order,start_sequence,end_sequence,created_at,updated_at)
                    SELECT {Guid.NewGuid()},{ids[i]},level_id,name,sort_order,start_sequence,end_sequence,created_at,updated_at
                    FROM document_structure_nodes WHERE parse_id={MigrationGoldenStates.ParseId};
                    """);
        }
        var foreignFile = Guid.NewGuid(); var foreignParse = Guid.NewGuid();
        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO document_files (id,file_name,content_type,structure_definition_id)
            SELECT {foreignFile},'another.pdf','application/pdf',structure_definition_id FROM document_files WHERE id={MigrationGoldenStates.FileId};
            INSERT INTO document_parses (id,document_file_id,model_version,status,parsed_at)
            VALUES ({foreignParse},{foreignFile},'vlm','parsed','2026-10-01T00:00:00Z');
            INSERT INTO document_structure_nodes (id,parse_id,level_id,name,sort_order,start_sequence,end_sequence,created_at,updated_at)
            SELECT {Guid.NewGuid()},{foreignParse},level_id,'Foreign chapter',sort_order,start_sequence,end_sequence,created_at,updated_at
            FROM document_structure_nodes WHERE parse_id={MigrationGoldenStates.ParseId};
            INSERT INTO document_structure_audio (id,parse_id,node_id,source_path,content_type,size_bytes,object_key,match_status,created_at,updated_at)
            SELECT {Guid.NewGuid()},{foreignParse},{Guid.NewGuid()},source_path,content_type,size_bytes,{"audio/foreign/" + foreignParse + ".wav"},match_status,created_at,updated_at
            FROM document_structure_audio WHERE parse_id={MigrationGoldenStates.ParseId};
            """);
        var before = await LegacyRowsAsync(context);
        await new DocthecaMigrationExecutor(context).ExecuteAsync();
        (await LegacyRowsAsync(context)).Should().Equal(before);
        (await context.DocumentStructureTrees.CountAsync()).Should().Be(6);
        var current = await context.DocumentFiles.AsNoTracking().SingleAsync(file => file.Id == MigrationGoldenStates.FileId);
        var expected = ids.Take(2).Max();
        (await context.DocumentStructureTrees.SingleAsync(tree => tree.Id == current.CurrentStructureTreeId)).OriginParseId.Should().Be(expected);
        (await context.DocumentStructureAudio.Select(audio => audio.TreeId).Distinct().CountAsync()).Should().Be(6);
        (await new DocumentStructureAudioRepository(context).GetByParseIdAsync(ids[2])).Should().ContainSingle();
        var stable = current.CurrentStructureTreeId;
        await context.DocumentParses.Where(parse => parse.Id == ids[3]).ExecuteUpdateAsync(set => set.SetProperty(parse => parse.ParsedAt, DateTimeOffset.UtcNow));
        (await context.DocumentFiles.AsNoTracking().SingleAsync(file => file.Id == MigrationGoldenStates.FileId)).CurrentStructureTreeId.Should().Be(stable);
        (await new DocthecaMigrationExecutor(context).InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.CurrentVersionCompatible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Down_ProvesRepresentability_RestoresExactSchemaAndRows_AndCanForwardAgain(bool sql)
    {
        using var context = fixture.CreateContext(_database);
        await context.GetService<IMigrator>().MigrateAsync(Previous);
        await PageMigrationTests.SeedAllEarlierTablesAsync(context);
        var rows = await LegacyRowsAsync(context);
        var columns = await MigrationGoldenStates.ReadColumnShapeAsync(context, DocthecaMigrationExecutor.PreOwnershipTableNames);
        var constraints = await MigrationGoldenStates.ReadConstraintShapeAsync(context, DocthecaMigrationExecutor.PreOwnershipTableNames);
        await new DocthecaMigrationExecutor(context).ExecuteAsync();
        if (sql) await MigrationGoldenStates.ExecuteRawAsync(context, await File.ReadAllTextAsync(StructureMigrationTests.RollbackScriptPath(Current)));
        else await context.GetService<IMigrator>().MigrateAsync(Previous);
        (await LegacyRowsAsync(context)).Should().Equal(rows);
        (await MigrationGoldenStates.ReadColumnShapeAsync(context, DocthecaMigrationExecutor.PreOwnershipTableNames)).Should().Equal(columns);
        (await MigrationGoldenStates.ReadConstraintShapeAsync(context, DocthecaMigrationExecutor.PreOwnershipTableNames)).Should().Equal(constraints);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(DocthecaMigrationExecutor.KnownMigrationIds.Take(7));
        await new DocthecaMigrationExecutor(context).ExecuteAsync();
        (await LegacyRowsAsync(context)).Should().Equal(rows);
    }

    [Theory]
    [InlineData("unbound", false)] [InlineData("unbound", true)]
    [InlineData("origin-deleted", false)] [InlineData("origin-deleted", true)]
    [InlineData("new-tree", false)] [InlineData("rebound", true)]
    [InlineData("origin-null", false)] [InlineData("origin-null", true)]
    public async Task NonrepresentableDown_RefusesWithoutChangingAnyState(string mutation, bool sql)
    {
        var seed = await SeedAsync();
        using var context = fixture.CreateContext(_database);
        if (mutation == "unbound") await context.DocumentStructureNodes.ExecuteUpdateAsync(set => set
            .SetProperty(node => node.ParseId, (Guid?)null).SetProperty(node => node.StartSequence, (int?)null).SetProperty(node => node.EndSequence, (int?)null));
        if (mutation == "origin-deleted") await new DocumentParseRepository(context).DeleteAsync(seed.Parse);
        if (mutation == "origin-null") await context.DocumentStructureAudio.ExecuteUpdateAsync(set => set.SetProperty(audio => audio.ParseId, (Guid?)null));
        if (mutation == "new-tree") await context.DocumentStructureTrees.ExecuteUpdateAsync(set => set.SetProperty(tree => tree.OriginParseId, (Guid?)null));
        if (mutation == "rebound")
        {
            var parse = await new DocumentParseRepository(context).AddAsync(new() { DocumentFileId = seed.File, Status = DocumentParseStatus.Parsed });
            await context.DocumentStructureNodes.ExecuteUpdateAsync(set => set.SetProperty(node => node.ParseId, (Guid?)parse.Id));
        }
        var before = await AllRowsAsync(context);
        var columns = await MigrationGoldenStates.ReadColumnShapeAsync(context, DocthecaMigrationExecutor.AllTableNames);
        var history = await MigrationGoldenStates.ReadAppliedHistoryAsync(context);
        var exception = await Record.ExceptionAsync(async () =>
        {
            if (sql) await MigrationGoldenStates.ExecuteRawAsync(context, await File.ReadAllTextAsync(StructureMigrationTests.RollbackScriptPath(Current)));
            else await context.GetService<IMigrator>().MigrateAsync(Previous);
        });
        exception.Should().NotBeNull();
        // A refused SQL script leaves its explicit transaction aborted; close it before fresh reads.
        await context.Database.CloseConnectionAsync();
        (await AllRowsAsync(context)).Should().Equal(before);
        (await MigrationGoldenStates.ReadColumnShapeAsync(context, DocthecaMigrationExecutor.AllTableNames)).Should().Equal(columns);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(history);
    }

    [Theory]
    [InlineData("missing-check")] [InlineData("wrong-check")] [InlineData("nullable-owner")]
    [InlineData("missing-fk")] [InlineData("unrecorded")] [InlineData("unknown")] [InlineData("gapped")]
    public async Task ConflictingOwnershipSchema_IsRefusedWithoutRepair(string change)
    {
        using var context = fixture.CreateContext(_database);
        await new DocthecaMigrationExecutor(context).ExecuteAsync();
        await MigrationGoldenStates.ExecuteRawAsync(context, change switch
        {
            "missing-check" => "ALTER TABLE document_structure_nodes DROP CONSTRAINT \"CK_document_structure_nodes_content_binding\"",
            "wrong-check" => "ALTER TABLE document_structure_nodes DROP CONSTRAINT \"CK_document_structure_nodes_content_binding\"; ALTER TABLE document_structure_nodes ADD CONSTRAINT \"CK_document_structure_nodes_content_binding\" CHECK (true)",
            "nullable-owner" => "ALTER TABLE document_structure_nodes ALTER COLUMN document_file_id DROP NOT NULL",
            "missing-fk" => "ALTER TABLE document_structure_nodes DROP CONSTRAINT \"FK_document_structure_nodes_document_structure_trees_document_~\"",
            "unrecorded" => "DELETE FROM \"__EFMigrationsHistory\"",
            "unknown" => "INSERT INTO \"__EFMigrationsHistory\" VALUES ('29990101000000_Unknown','10.0.13')",
            _ => "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"='20261024021500_AddRequiredStructureLevels'"
        });
        var rows = await AllRowsAsync(context);
        var history = await MigrationGoldenStates.ReadAppliedHistoryAsync(context);
        var executor = new DocthecaMigrationExecutor(context);
        (await executor.InspectAsync()).Should().Be(change == "unknown" ? DocthecaMigrationExecutor.DatabaseState.VersionTooNew : DocthecaMigrationExecutor.DatabaseState.InspectionFailed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync());
        (await AllRowsAsync(context)).Should().Equal(rows);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(history);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SuccessfulCompletion_DetachesOnlyCurrentBinding_AndCannotReplayAfterExplicitRebind(bool late)
    {
        var seed = await SeedAsync();
        using var context = fixture.CreateContext(_database);
        var repository = new DocumentParseRepository(context);
        var job = await repository.AddAsync(new() { DocumentFileId = seed.File, Status = DocumentParseStatus.Parsing });
        var tree = await context.DocumentFiles.Select(file => file.CurrentStructureTreeId).SingleAsync();
        var before = (await new DocumentStructureRepository(context).GetByParseIdAsync(seed.Parse)).Single();
        var audio = await context.DocumentStructureAudio.AsNoTracking().SingleAsync();
        await repository.CompleteAsync(job.Id, Guid.NewGuid(), "new", [], [], [Block(job.Id, 0)]);
        var detached = (await new InternalStructureRepository(context.Options()).ReadTreeAsync(seed.File, default)).Tree!;
        detached.ParseId.Should().BeNull();
        detached.Nodes.Single().Id.Should().Be(seed.Node);
        detached.Nodes.Single().StartSequence.Should().BeNull();
        var content = await new InternalStructureRepository(context.Options()).ReadContentAsync(seed.Node, new(null, 10, null), default);
        content.Page!.Items.Should().BeEmpty(); content.Page.HasMore.Should().BeFalse();
        if (late)
        {
            before.ParseId = job.Id;
            (await new DocumentStructureRepository(context).ReplaceAsync(job.Id, [before])).Should().BeTrue();
            await repository.CompleteAsync(job.Id, Guid.NewGuid(), "replayed", [], [], []);
            (await context.DocumentStructureNodes.AsNoTracking().SingleAsync()).ParseId.Should().Be(job.Id);
        }
        await repository.DeleteAsync(seed.Parse);
        (await context.DocumentFiles.AsNoTracking().SingleAsync()).CurrentStructureTreeId.Should().Be(tree);
        var retained = await context.DocumentStructureNodes.AsNoTracking().SingleAsync();
        retained.Id.Should().Be(before.Id); retained.Name.Should().Be(before.Name); retained.SortOrder.Should().Be(before.SortOrder);
        var retainedAudio = await context.DocumentStructureAudio.AsNoTracking().SingleAsync();
        JsonSerializer.Serialize(retainedAudio).Should().Be(JsonSerializer.Serialize(audio));
        (await new InternalStructureRepository(context.Options()).ReadAudioAsync(seed.Node, retainedAudio.Id, default))!.Items.Should().ContainSingle();
    }

    [Theory]
    [InlineData("history-delete", false)] [InlineData("history-save", false)]
    [InlineData("history-delete", true)] [InlineData("history-save", true)]
    public async Task CompletionBlockedBeforeOwnerLock_HistoryChangesDoNotHideCurrentBindingChanges(string mutation, bool rebind)
    {
        var seed = await SeedAsync();
        using var setup = fixture.CreateContext(_database);
        var structures = new DocumentStructureRepository(setup);
        var before = (await structures.GetByParseIdAsync(seed.Parse)).Single();
        var audio = await setup.DocumentStructureAudio.AsNoTracking().SingleAsync();
        var history = await new DocumentParseRepository(setup).AddAsync(new() { DocumentFileId = seed.File, Status = DocumentParseStatus.Parsed });
        var historyTree = new DocumentStructureTreeEntity { DocumentFileId = seed.File, OriginParseId = history.Id };
        setup.DocumentStructureTrees.Add(historyTree); await setup.SaveChangesAsync();
        var historyNode = new DocumentStructureNodeEntity {
            DocumentFileId = seed.File, TreeId = historyTree.Id, ParseId = history.Id, LevelId = before.LevelId,
            Name = "Historical chapter", StartSequence = 0, EndSequence = 0 };
        setup.DocumentStructureNodes.Add(historyNode);
        setup.DocumentParseBlocks.Add(new() { ParseId = history.Id, Sequence = 0, BlockType = "text", TextContent = "Historical text", BlockData = "{}" });
        await setup.SaveChangesAsync(); setup.ChangeTracker.Clear();
        var currentTree = (await setup.DocumentFiles.AsNoTracking().SingleAsync()).CurrentStructureTreeId;
        var job = await new DocumentParseRepository(setup).AddAsync(new() { DocumentFileId = seed.File, Status = DocumentParseStatus.Parsing });
        var barrier = new OwnerBarrier(); using var delayed = Context(barrier);
        var completion = new DocumentParseRepository(delayed).CompleteAsync(job.Id, Guid.NewGuid(), "new result", [], [], []);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (mutation == "history-delete") await new DocumentParseRepository(setup).DeleteAsync(history.Id);
            else (await structures.ReplaceAsync(history.Id, await structures.GetByParseIdAsync(history.Id))).Should().BeTrue();
            // A real current-tree edit still protects its newer binding from the waiting completion.
            if (rebind) (await structures.ReplaceAsync(seed.Parse, [before])).Should().BeTrue();
        }
        finally { barrier.Resume.TrySetResult(); }
        await completion;

        (await setup.DocumentParses.AsNoTracking().SingleAsync(parse => parse.Id == job.Id)).Status.Should().Be(DocumentParseStatus.Parsed);
        (await setup.DocumentFiles.AsNoTracking().SingleAsync()).CurrentStructureTreeId.Should().Be(currentTree);
        var retained = await setup.DocumentStructureNodes.AsNoTracking().SingleAsync(node => node.Id == seed.Node);
        retained.Should().BeEquivalentTo(before, options => options.ExcludingMissingMembers()
            .Excluding(node => node.ParseId).Excluding(node => node.StartSequence).Excluding(node => node.EndSequence));
        retained.ParseId.Should().Be(rebind ? seed.Parse : null);
        retained.StartSequence.Should().Be(rebind ? before.StartSequence : null);
        retained.EndSequence.Should().Be(rebind ? before.EndSequence : null);
        JsonSerializer.Serialize(await setup.DocumentStructureAudio.AsNoTracking().SingleAsync()).Should().Be(JsonSerializer.Serialize(audio));
        var content = await new InternalStructureRepository(setup.Options()).ReadContentAsync(seed.Node, new(null, 10, null), default);
        content.Page!.Items.Should().HaveCount(rebind ? 1 : 0);
        content.Page.HasMore.Should().BeFalse();
        var retainedHistory = await setup.DocumentStructureNodes.AsNoTracking().SingleAsync(node => node.Id == historyNode.Id);
        retainedHistory.ParseId.Should().Be(mutation == "history-delete" ? null : history.Id);
        retainedHistory.TreeId.Should().Be(historyTree.Id);
        // A retry of the successful result cannot remove the subsequently protected binding either.
        await new DocumentParseRepository(setup).CompleteAsync(job.Id, Guid.NewGuid(), "replayed", [], [], []);
        (await setup.DocumentStructureNodes.AsNoTracking().SingleAsync(node => node.Id == seed.Node)).ParseId.Should().Be(rebind ? seed.Parse : null);
    }

    [Theory]
    [InlineData("rebind")] [InlineData("completion")] [InlineData("parse-delete")]
    public async Task CompletionBlockedBeforeOwnerLock_RespectsLaterCommittedGeneration(string earlier)
    {
        var seed = await SeedAsync();
        using var setup = fixture.CreateContext(_database);
        var job = await new DocumentParseRepository(setup).AddAsync(new() { DocumentFileId = seed.File, Status = DocumentParseStatus.Parsing });
        var second = await new DocumentParseRepository(setup).AddAsync(new() { DocumentFileId = seed.File, Status = DocumentParseStatus.Parsing });
        var barrier = new OwnerBarrier();
        using var delayed = Context(barrier);
        var completion = new DocumentParseRepository(delayed).CompleteAsync(job.Id, Guid.NewGuid(), "late", [], [], [Block(job.Id, 0)]);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (earlier == "rebind")
            {
                var nodes = await new DocumentStructureRepository(setup).GetByParseIdAsync(seed.Parse);
                (await new DocumentStructureRepository(setup).ReplaceAsync(seed.Parse, nodes)).Should().BeTrue();
            }
            if (earlier == "completion") await new DocumentParseRepository(setup).CompleteAsync(second.Id, Guid.NewGuid(), "first", [], [], []);
            if (earlier == "parse-delete") await new DocumentParseRepository(setup).DeleteAsync(seed.Parse);
        }
        finally { barrier.Resume.TrySetResult(); }
        await completion;
        var node = await setup.DocumentStructureNodes.AsNoTracking().SingleAsync();
        node.ParseId.Should().Be(earlier == "rebind" ? seed.Parse : null);
        (await setup.DocumentStructureAudio.AsNoTracking().SingleAsync()).NodeId.Should().Be(seed.Node);
        (await setup.DocumentParses.AsNoTracking().SingleAsync(parse => parse.Id == job.Id)).Status.Should().Be(DocumentParseStatus.Parsed);
    }

    [Theory]
    [InlineData("tree-save")] [InlineData("parse-delete")] [InlineData("document-delete")]
    public async Task AudioInsertBlockedBeforeOwnerLock_RevalidatesCommittedOwnerAndTree(string earlier)
    {
        var seed = await SeedAsync();
        var barrier = new OwnerBarrier(); using var delayed = Context(barrier);
        var item = new DocumentStructureAudioModel { Id = Guid.NewGuid(), ParseId = seed.Parse, NodeId = seed.Node, SourcePath = "new.wav", ObjectKey = "audio/new.wav", ContentType = "audio/wav" };
        var insert = new DocumentStructureAudioRepository(delayed).InsertAsync(seed.Parse, [item]);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var setup = fixture.CreateContext(_database);
        try
        {
            if (earlier == "tree-save") (await new DocumentStructureRepository(setup).ReplaceAsync(seed.Parse, [])).Should().BeTrue();
            if (earlier == "parse-delete") await new DocumentParseRepository(setup).DeleteAsync(seed.Parse);
            if (earlier == "document-delete") (await new DocumentFileRepository(setup).DeleteWithAudioAsync(seed.File))!.Should().ContainSingle();
        }
        finally { barrier.Resume.TrySetResult(); }
        // New imports require current nodes at commit as well as preflight; a removed whole tree now rejects the insert.
        (await insert).Should().BeFalse();
        (await setup.DocumentStructureAudio.AnyAsync(audio => audio.Id == item.Id)).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LocalCompletionFailureOrCancellation_RollsBackEveryResultAndBinding(bool cancel)
    {
        var seed = await SeedAsync();
        using var setup = fixture.CreateContext(_database);
        var job = await new DocumentParseRepository(setup).AddAsync(new() { DocumentFileId = seed.File, Status = DocumentParseStatus.Parsing });
        using var cancellation = new CancellationTokenSource();
        var before = await AllRowsAsync(setup);
        using var context = Context(new AbortReplacement(cancellation, cancel));
        var error = await Record.ExceptionAsync(() => new DocumentParseRepository(context).CompleteAsync(job.Id, Guid.NewGuid(), "new", [], [], [], cancellation.Token));
        error.Should().NotBeNull(); if (cancel) error.Should().BeAssignableTo<OperationCanceledException>();
        (await AllRowsAsync(setup)).Should().Equal(before);
        await new DocumentParseRepository(setup).CompleteAsync(job.Id, Guid.NewGuid(), "retry", [], [], []);
        (await setup.DocumentStructureNodes.AsNoTracking().SingleAsync()).ParseId.Should().BeNull();
    }

    [Fact]
    public async Task ExactKeyLease_BlocksCleanupUntilMetadataOutcome_AndProtectsHistoricalReferences()
    {
        var seed = await SeedAsync();
        using var setup = fixture.CreateContext(_database);
        var repo = new DocumentStructureAudioRepository(setup);
        var key = (await setup.DocumentStructureAudio.SingleAsync()).ObjectKey;
        await new DocumentParseRepository(setup).DeleteAsync(seed.Parse);
        await using var held = await repo.AcquireObjectKeyLeaseAsync([key]);
        var connection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reader = fixture.CreateContext(_database);
        var pending = new DocumentStructureAudioRepository(reader).AcquireObjectKeyLeaseAsync([key]);
        // Observe the PostgreSQL advisory-lock wait rather than depending on a scheduler delay.
        var waiting = false;
        for (var i = 0; i < 100 && !waiting; i++)
        {
            waiting = await setup.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype='advisory' AND NOT granted) AS \"Value\"").SingleAsync();
            if (!waiting) await Task.Yield();
        }
        waiting.Should().BeTrue(); pending.IsCompleted.Should().BeFalse();
        await held.DisposeAsync();
        await using var admitted = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        (await new DocumentStructureAudioRepository(reader).GetReferencedObjectKeysAsync([key])).Should().Contain(key);
    }

    public static IEnumerable<object[]> EarlierHistories => DocthecaMigrationExecutor.KnownMigrationIds.Take(7).Select(id => new object[] { id });

    [Theory]
    [MemberData(nameof(EarlierHistories))]
    public async Task EverySupportedHistory_ReachesTheSameFinalModel(string history)
    {
        using var context = fixture.CreateContext(_database);
        await context.GetService<IMigrator>().MigrateAsync(history);
        await MigrationGoldenStates.SeedBusinessRowsAsync(context);
        await new DocthecaMigrationExecutor(context).ExecuteAsync();
        (await new DocthecaMigrationExecutor(context).InspectAsync()).Should().Be(DocthecaMigrationExecutor.DatabaseState.CurrentVersionCompatible);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(DocthecaMigrationExecutor.KnownMigrationIds);
        var reference = await fixture.CreateDatabaseAsync();
        try
        {
            using var model = fixture.CreateContext(reference); await model.Database.EnsureCreatedAsync();
            (await MigrationGoldenStates.ReadColumnShapeAsync(context, DocthecaMigrationExecutor.AllTableNames)).Order().Should()
                .Equal((await MigrationGoldenStates.ReadColumnShapeAsync(model, DocthecaMigrationExecutor.AllTableNames)).Order());
            (await MigrationGoldenStates.ReadConstraintShapeAsync(context, DocthecaMigrationExecutor.AllTableNames)).Should()
                .Equal(await MigrationGoldenStates.ReadConstraintShapeAsync(model, DocthecaMigrationExecutor.AllTableNames));
        }
        finally { await fixture.DropDatabaseAsync(reference); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OwnershipMigrationFailureOrCancellation_RollsBackNewSchemaAndHistory_ThenRetries(bool cancel)
    {
        using var setup = fixture.CreateContext(_database);
        await setup.GetService<IMigrator>().MigrateAsync(Previous); await PageMigrationTests.SeedAllEarlierTablesAsync(setup);
        var rows = await LegacyRowsAsync(setup); var history = await MigrationGoldenStates.ReadAppliedHistoryAsync(setup);
        var columns = await MigrationGoldenStates.ReadColumnShapeAsync(setup, DocthecaMigrationExecutor.PreOwnershipTableNames);
        using var cancellation = new CancellationTokenSource();
        using (var attempt = Context(new AbortOwnershipMigration(cancellation, cancel)))
        {
            var error = await Record.ExceptionAsync(() => new DocthecaMigrationExecutor(attempt).ExecuteAsync(cancellation.Token));
            error.Should().NotBeNull(); if (cancel) error.Should().BeAssignableTo<OperationCanceledException>();
        }
        (await LegacyRowsAsync(setup)).Should().Equal(rows);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(setup)).Should().Equal(history);
        (await MigrationGoldenStates.ReadColumnShapeAsync(setup, DocthecaMigrationExecutor.PreOwnershipTableNames)).Should().Equal(columns);
        (await MigrationGoldenStates.ScalarAsync<bool>(setup, "SELECT to_regclass('public.document_structure_trees') IS NULL")).Should().BeTrue();
        await new DocthecaMigrationExecutor(setup).ExecuteAsync();
        (await LegacyRowsAsync(setup)).Should().Equal(rows);
    }

    [Fact]
    public async Task AudioLinkSaveBlockedBeforeOwnerLock_CannotRelinkAChapterRemovedWhileWaiting()
    {
        var seed = await SeedAsync(); using var setup = fixture.CreateContext(_database);
        var audio = await setup.DocumentStructureAudio.AsNoTracking().SingleAsync();
        var barrier = new OwnerBarrier(); using var delayed = Context(barrier);
        var saving = new DocumentStructureAudioRepository(delayed).UpdateLinksAsync(seed.Parse, [new(audio.Id, seed.Node)], [], DateTimeOffset.UtcNow);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { (await new DocumentStructureRepository(setup).ReplaceAsync(seed.Parse, [])).Should().BeTrue(); }
        finally { barrier.Resume.TrySetResult(); }
        (await saving).Status.Should().Be(AudioLinkUpdateStatus.NodeNotFound);
        (await setup.DocumentStructureAudio.AsNoTracking().SingleAsync()).NodeId.Should().BeNull();
        (await setup.DocumentStructureAudio.AsNoTracking().SingleAsync()).ObjectKey.Should().Be(audio.ObjectKey);
    }

    private sealed class AbortOwnershipMigration(CancellationTokenSource cancellation, bool cancel) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("CREATE TABLE document_structure_trees", StringComparison.Ordinal))
            { if (cancel) cancellation.Cancel(); else throw new InvalidOperationException("Synthetic migration failure"); }
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task LostUploadLeaseSession_CannotCommitMetadataOrTreatAnUncertainReferenceAsUnowned()
    {
        var seed = await SeedAsync(); using var context = fixture.CreateContext(_database);
        var repo = new DocumentStructureAudioRepository(context);
        var key = "audio/lost-session.wav";
        await using var lease = await repo.AcquireObjectKeyLeaseAsync([key]);
        var backend = ((Npgsql.NpgsqlConnection)context.Database.GetDbConnection()).ProcessID;
        using var control = fixture.CreateContext(_database);
        await control.Database.ExecuteSqlAsync($"SELECT pg_terminate_backend({backend})");
        var item = new DocumentStructureAudioModel { Id=Guid.NewGuid(), ParseId=seed.Parse, SourcePath="lost.wav", ObjectKey=key, ContentType="audio/wav" };
        (await Record.ExceptionAsync(() => repo.InsertAsync(seed.Parse, [item]))).Should().NotBeNull();
        (await control.DocumentStructureAudio.AnyAsync(audio => audio.Id == item.Id)).Should().BeFalse();
        (await Record.ExceptionAsync(() => repo.GetReferencedObjectKeysAsync([key]))).Should().NotBeNull();
    }

    private async Task<(Guid File, Guid Parse, Guid Node)> SeedAsync()
    {
        using var context = fixture.CreateContext(_database);
        await context.GetService<IMigrator>().MigrateAsync(Previous);
        await PageMigrationTests.SeedAllEarlierTablesAsync(context);
        await context.Database.ExecuteSqlAsync($"UPDATE document_parses SET status='parsed'");
        await new DocthecaMigrationExecutor(context).ExecuteAsync();
        return (MigrationGoldenStates.FileId, MigrationGoldenStates.ParseId, await context.DocumentStructureNodes.Select(node => node.Id).SingleAsync());
    }
    private DocthecaDbContext Context(IInterceptor interceptor) => new(new DbContextOptionsBuilder<DocthecaDbContext>()
        .UseNpgsql(fixture.GetConnectionString(_database)).AddInterceptors(interceptor).Options);
    private static DocumentParseBlockModel Block(Guid parse, int sequence) => new() { Id = Guid.NewGuid(), ParseId = parse, Sequence = sequence, BlockType = "text", BlockData = "{}" };
    private static Task<List<string>> LegacyRowsAsync(DocthecaDbContext context) => RowsAsync(context, false);
    private static Task<List<string>> AllRowsAsync(DocthecaDbContext context) => RowsAsync(context, true);
    private static async Task<List<string>> RowsAsync(DocthecaDbContext context, bool all)
    {
        var rows = new List<string>();
        foreach (var table in all ? DocthecaMigrationExecutor.AllTableNames : DocthecaMigrationExecutor.PreOwnershipTableNames)
        {
            await context.Database.OpenConnectionAsync();
            try
            {
                await using var command = context.Database.GetDbConnection().CreateCommand();
                var projection = all ? "to_jsonb(t)" : "to_jsonb(t) - 'current_structure_tree_id' - 'structure_binding_generation' - 'expected_binding_generation' - 'tree_id'" + (table is "document_structure_nodes" or "document_structure_audio" ? " - 'document_file_id'" : "");
                command.CommandText = $"SELECT '{table}:' || ({projection})::text FROM {table} t ORDER BY 1";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
            }
            finally { await context.Database.CloseConnectionAsync(); }
        }
        return rows;
    }
    private sealed class OwnerBarrier : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("document_files", StringComparison.Ordinal) && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
            { Entered.TrySetResult(); await Resume.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }
    private sealed class AbortReplacement(CancellationTokenSource cancellation, bool cancel) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("DELETE FROM document_parse_pages", StringComparison.Ordinal))
            { if (cancel) cancellation.Cancel(); else throw new InvalidOperationException("Synthetic replacement failure"); }
            return ValueTask.FromResult(result);
        }
    }
}

internal static class OwnedContextOptions
{
    internal static DbContextOptions<DocthecaDbContext> Options(this DocthecaDbContext context) =>
        new DbContextOptionsBuilder<DocthecaDbContext>().UseNpgsql(context.Database.GetConnectionString()).Options;
}
