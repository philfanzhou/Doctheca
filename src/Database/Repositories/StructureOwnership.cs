using Doctheca.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Doctheca.Database.Repositories;

/// <summary>All mutations take the document lock before touching parse/tree rows.</summary>
internal static class StructureOwnership
{
    internal static async Task<DocumentFileEntity?> LockDocumentAsync(DocthecaDbContext context, Guid documentId, CancellationToken ct = default)
    {
        var ids = await context.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM document_files WHERE id = {documentId} FOR UPDATE").ToListAsync(ct);
        if (ids.Count == 0) return null;
        // Do not reuse an entity read before the lock (e.g. an active worker's job).
        var file = await context.DocumentFiles.SingleAsync(file => file.Id == documentId, ct);
        await context.Entry(file).ReloadAsync(ct);
        return file;
    }

    internal static async Task<DocumentFileEntity?> LockParseOwnerAsync(DocthecaDbContext context, Guid parseId, CancellationToken ct = default)
    {
        var owner = await context.DocumentParses.AsNoTracking().Where(parse => parse.Id == parseId)
            .Select(parse => (Guid?)parse.DocumentFileId).SingleOrDefaultAsync(ct);
        if (owner is not { } documentId) return null;
        var file = await LockDocumentAsync(context, documentId, ct);
        return file is not null && await context.DocumentParses.AnyAsync(parse => parse.Id == parseId && parse.DocumentFileId == documentId, ct) ? file : null;
    }

    internal static async Task<Guid?> TreeForParseAsync(DocthecaDbContext context, Guid parseId, CancellationToken ct = default)
    {
        var historical = await context.DocumentStructureTrees.Where(tree => tree.OriginParseId == parseId)
            .Select(tree => (Guid?)tree.Id).SingleOrDefaultAsync(ct);
        if (historical is not null) return historical;
        return await context.DocumentParses.Where(parse => parse.Id == parseId)
            .Select(parse => parse.DocumentFile!.CurrentStructureTreeId).SingleOrDefaultAsync(ct);
    }
}
