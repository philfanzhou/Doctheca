using System.Data;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Doctheca.Database.Repositories;

public sealed partial class InternalStructureRepository : IInternalAudioRepository
{
    public async Task<InternalAudioMembership?> ReadAudioAsync(Guid nodeId, Guid? audioId, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        var node = await CurrentNodeAsync(context, nodeId, cancellationToken);
        if (node is null) return null;
        var rows = await context.DocumentStructureAudio.AsNoTracking()
            .Where(audio => audio.TreeId == node.TreeId && audio.DocumentFileId == node.DocumentFileId && audio.NodeId == nodeId && (audioId == null || audio.Id == audioId))
            .Select(audio => new InternalAudioObject(audio.Id, audio.ParseId, nodeId, audio.SourcePath,
                audio.ContentType, audio.SizeBytes, audio.ObjectKey, audio.DocumentFileId)).ToListAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        return new(node, rows.OrderBy(audio => audio.SourcePath, StringComparer.Ordinal).ThenBy(audio => audio.Id).ToArray());
    }
}
