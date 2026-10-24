using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Doctheca.Tests;

/// <summary>Explicit current-model ownership for fixtures that seed legacy-shaped bound rows.</summary>
internal static class OwnedStructureFixture
{
    internal static async Task<int> SaveOwnedChangesAsync(this DocthecaDbContext context, CancellationToken ct = default)
    {
        var nodes = context.ChangeTracker.Entries<DocumentStructureNodeEntity>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.TreeId == Guid.Empty).Select(entry => entry.Entity).ToArray();
        var audio = context.ChangeTracker.Entries<DocumentStructureAudioEntity>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.TreeId == Guid.Empty).Select(entry => entry.Entity).ToArray();
        var files = new HashSet<Guid>();
        foreach (var parseId in nodes.Select(node => node.ParseId!.Value).Concat(audio.Select(item => item.ParseId!.Value)).Distinct())
        {
            var parse = context.ChangeTracker.Entries<DocumentParseEntity>().Select(entry => entry.Entity).FirstOrDefault(item => item.Id == parseId)
                ?? await context.DocumentParses.SingleAsync(item => item.Id == parseId, ct);
            var tree = context.ChangeTracker.Entries<DocumentStructureTreeEntity>().Select(entry => entry.Entity).FirstOrDefault(item => item.OriginParseId == parseId)
                ?? await context.DocumentStructureTrees.SingleOrDefaultAsync(item => item.OriginParseId == parseId, ct);
            if (tree is null)
            {
                tree = new DocumentStructureTreeEntity { DocumentFileId = parse.DocumentFileId, OriginParseId = parseId };
                context.DocumentStructureTrees.Add(tree);
            }
            foreach (var node in nodes.Where(node => node.ParseId == parseId))
            {
                node.DocumentFileId = parse.DocumentFileId; node.TreeId = tree.Id;
                node.StartSequence ??= 0; node.EndSequence ??= 0;
            }
            foreach (var item in audio.Where(item => item.ParseId == parseId))
            { item.DocumentFileId = parse.DocumentFileId; item.TreeId = tree.Id; }
            files.Add(parse.DocumentFileId);
        }
        var changed = await context.SaveChangesAsync(ct);
        foreach (var id in files)
        {
            var file = await context.DocumentFiles.FindAsync([id], ct);
            if (file!.CurrentStructureTreeId is not null) continue;
            file.CurrentStructureTreeId = await (from tree in context.DocumentStructureTrees
                join parse in context.DocumentParses on tree.OriginParseId equals parse.Id
                where tree.DocumentFileId == id && parse.Status == DocumentParseStatus.Parsed && context.DocumentStructureNodes.Any(node => node.TreeId == tree.Id)
                orderby parse.ParsedAt.HasValue descending, parse.ParsedAt descending, parse.Id descending
                select (Guid?)tree.Id).FirstOrDefaultAsync(ct);
        }
        if (files.Count > 0) await context.SaveChangesAsync(ct);
        return changed;
    }
}
