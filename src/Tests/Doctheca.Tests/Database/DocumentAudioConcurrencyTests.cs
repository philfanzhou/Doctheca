using System.Data.Common;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Database.Repositories;
using Doctheca.Domain.Models.Structure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Doctheca.Tests.Database;

public sealed partial class DocumentAuthoringConcurrencyTests
{
    [Theory]
    [InlineData("policy")] [InlineData("node")] [InlineData("document")] [InlineData("other-review")] [InlineData("parse-complete")] [InlineData("parse-delete")]
    public async Task DocumentAudioCommit_RechecksCommittedPolicyTreeAndWholeRetainedSetAfterOwnerWait(string action)
    {
        var s = await Seed(); using var setup = Context();
        var old = await setup.DocumentStructureAudio.AsNoTracking().SingleAsync();
        var tree = (await new DocumentAuthoringRepository(setup).GetTreeAsync(s.File, default))!;
        var addition = new DocumentStructureAudioModel { Id = Guid.NewGuid(), DocumentFileId = s.File, TreeId = tree.TreeId!.Value,
            NodeId = action == "policy" ? null : s.Node, SourcePath = "new.wav", ObjectKey = "audio/new-exact-key.wav", ContentType = "audio/wav", SizeBytes = 48044,
            MatchStatus = AudioMatchStatus.Matched, LinkSource = AudioLinkSource.Manual, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var barrier = new OwnerBarrier(); using var delayed = Context(barrier);
        var pending = new DocumentStructureAudioRepository(delayed).CommitAsync(new(s.File, true), [addition], [], [], DateTimeOffset.UtcNow);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (action == "policy")
            {
                var definition = (await new StructureDefinitionRepository(setup).GetByIdAsync(await setup.StructureLevels.Select(l => l.DefinitionId).SingleAsync()))!;
                definition.Levels.Single().Required = true;
                (await new StructureDefinitionRepository(setup).UpdateAsync(definition)).Should().BeTrue();
            }
            if (action == "node") await new DocumentAuthoringRepository(setup).SaveTreeAsync(s.File, [Node(s.Level, "Replacement")], default);
            if (action == "document") await new DocumentFileRepository(setup).DeleteAsync(s.File);
            if (action == "other-review") (await new DocumentStructureAudioRepository(setup).CommitAsync(new(s.File, true), [], [new(s.Audio, null)], [], DateTimeOffset.UtcNow)).Status.Should().Be(AudioLinkUpdateStatus.Ok);
            if (action == "parse-delete") await new DocumentParseRepository(setup).DeleteAsync(s.Parse);
            if (action == "parse-complete")
            {
                var job = await new DocumentParseRepository(setup).AddAsync(new() { DocumentFileId = s.File });
                await new DocumentParseRepository(setup).CompleteAsync(job.Id, Guid.NewGuid(), "new text", [], [], []);
            }
        }
        finally { barrier.Resume.TrySetResult(); }
        var result = await pending;
        result.Status.Should().Be(action switch { "policy" => AudioLinkUpdateStatus.Invalid, "node" => AudioLinkUpdateStatus.NodeNotFound,
            "document" => AudioLinkUpdateStatus.DocumentNotFound, _ => AudioLinkUpdateStatus.Ok });
        var rows = await setup.DocumentStructureAudio.AsNoTracking().OrderBy(a => a.SourcePath).ToListAsync();
        if (action == "document") rows.Should().BeEmpty();
        else
        {
            rows.Single(a => a.Id == old.Id).ObjectKey.Should().Be(old.ObjectKey);
            rows.Single(a => a.Id == old.Id).NodeId.Should().Be(action is "node" or "other-review" ? null : old.NodeId);
            rows.Any(a => a.Id == addition.Id).Should().Be(action is "other-review" or "parse-complete" or "parse-delete");
        }
    }

    [Fact]
    public async Task DefinitionReplacement_WaitsForAudioCommitDefinitionShare_ThenMarksRetainedAudioForReview()
    {
        var s = await Seed(); using var setup = Context();
        var definitionId = await setup.StructureLevels.Select(l => l.DefinitionId).SingleAsync();
        var barrier = new ShareBarrier(); using var delayed = Context(barrier);
        var pending = new DocumentStructureAudioRepository(delayed).CommitAsync(new(s.File, true), [], [new(s.Audio, null)], [], DateTimeOffset.UtcNow);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var writer = Context(); await writer.Database.OpenConnectionAsync();
        var pid = ((Npgsql.NpgsqlConnection)writer.Database.GetDbConnection()).ProcessID;
        var definition = (await new StructureDefinitionRepository(writer).GetByIdAsync(definitionId))!;
        definition.Levels.Single().Required = true;
        var replacement = new StructureDefinitionRepository(writer).UpdateAsync(definition);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!(await setup.Database.SqlQuery<int>($"SELECT cardinality(pg_blocking_pids({pid})) AS \"Value\"").SingleAsync(deadline.Token)).Equals(1))
                await Task.Delay(10, deadline.Token);
            replacement.IsCompleted.Should().BeFalse();
        }
        finally { barrier.Resume.TrySetResult(); }
        (await pending).Status.Should().Be(AudioLinkUpdateStatus.Ok); (await replacement).Should().BeTrue();
        var scope = (await new DocumentStructureAudioRepository(setup).GetScopeAsync(new(s.File, true)))!;
        new Doctheca.Domain.Services.Structure.AudioRequiredPolicy(scope.Levels, scope.Nodes.ToDictionary(n => n.Id)).Reason(null).Should().NotBeNull();
        var row = await setup.DocumentStructureAudio.AsNoTracking().SingleAsync(); row.Id.Should().Be(s.Audio); row.NodeId.Should().BeNull(); row.ObjectKey.Should().Be("audio/preserved.wav");
    }
    [Fact]
    public async Task ConcurrentDocumentReviews_ApplyEachCompleteSetOnce_WithoutAdvancingContentGeneration()
    {
        var s = await Seed(); using var setup = Context(); var node2 = Guid.NewGuid();
        var tree = (await new DocumentAuthoringRepository(setup).SaveTreeAsync(s.File, [Node(s.Level, "One", s.Node), Node(s.Level, "Two", node2)], default)).Value!.Tree;
        var audio2 = new DocumentStructureAudioEntity { DocumentFileId = s.File, TreeId = tree.TreeId!.Value, NodeId = s.Node,
            SourcePath = "second.wav", ObjectKey = "audio/second-preserved.wav", ContentType = "audio/wav", SizeBytes = 48044 };
        setup.DocumentStructureAudio.Add(audio2); await setup.SaveChangesAsync();
        var generation = await setup.DocumentFiles.Select(f => f.StructureBindingGeneration).SingleAsync();
        var barrier = new OwnerBarrier(); using var delayed = Context(barrier);
        var pending = new DocumentStructureAudioRepository(delayed).CommitAsync(new(s.File, true), [], [new(s.Audio, node2), new(audio2.Id, s.Node)], [], DateTimeOffset.UtcNow);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { (await new DocumentStructureAudioRepository(setup).CommitAsync(new(s.File, true), [], [new(s.Audio, null), new(audio2.Id, node2)], [], DateTimeOffset.UtcNow)).Status.Should().Be(AudioLinkUpdateStatus.Ok); }
        finally { barrier.Resume.TrySetResult(); }
        (await pending).Status.Should().Be(AudioLinkUpdateStatus.Ok);
        var rows = await setup.DocumentStructureAudio.AsNoTracking().ToDictionaryAsync(a => a.Id);
        rows[s.Audio].NodeId.Should().Be(node2); rows[audio2.Id].NodeId.Should().Be(s.Node);
        rows[s.Audio].ObjectKey.Should().Be("audio/preserved.wav"); rows[audio2.Id].ObjectKey.Should().Be("audio/second-preserved.wav");
        (await setup.DocumentFiles.AsNoTracking().Select(f => f.StructureBindingGeneration).SingleAsync()).Should().Be(generation);
    }

    private sealed class ShareBarrier : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("structure_definitions") && command.CommandText.Contains("FOR SHARE"))
            { Entered.TrySetResult(); await Resume.Task.WaitAsync(ct); }
            return result;
        }
    }
}
