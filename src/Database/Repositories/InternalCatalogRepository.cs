using System.Data;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Doctheca.Database.Repositories;

public sealed class InternalCatalogRepository(DbContextOptions<DocthecaDbContext> options) : IInternalCatalogRepository
{
    // EF query/command errors can contain raw provider details before the HTTP boundary sees
    // them. Isolate anonymous reads from that logging; the marked endpoint's safe exception
    // handler owns diagnostics. Existing administrative contexts retain their logging.
    private DocthecaDbContext CreateContext() => new(new DbContextOptionsBuilder<DocthecaDbContext>(options)
        .UseLoggerFactory(NullLoggerFactory.Instance).EnableSensitiveDataLogging(false).EnableDetailedErrors(false).Options);

    public async Task<IReadOnlyList<InternalDefinition>> ReadDefinitionsAsync(
        InternalCatalogQuery query, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var definitions = context.StructureDefinitions.AsNoTracking();
        if (query.After is { } after) definitions = definitions.Where(item => item.Id.CompareTo(after) > 0);
        var rows = await definitions.OrderBy(item => item.Id).Take(query.PageSize + 1)
            .Select(item => new { item.Id, item.Name }).ToListAsync(cancellationToken);
        var ids = rows.Select(item => item.Id).ToArray();
        var levels = await context.StructureLevels.AsNoTracking().Where(item => ids.Contains(item.DefinitionId))
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
            .Select(item => new { item.DefinitionId, Level = new InternalLevel(item.Id, item.Name, item.ParentLevelId, item.SortOrder) })
            .ToListAsync(cancellationToken);
        var byDefinition = levels.ToLookup(item => item.DefinitionId, item => item.Level);
        await transaction.CommitAsync(cancellationToken);
        return rows.Select(item => new InternalDefinition(item.Id, item.Name, byDefinition[item.Id].ToList())).ToList();
    }

    public async Task<InternalDocumentPage> ReadDocumentsAsync(InternalCatalogQuery query, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        if (query.StructureDefinitionId is { } requested &&
            !await context.StructureDefinitions.AnyAsync(item => item.Id == requested, cancellationToken))
            return new InternalDocumentPage(false, []);

        // Only persisted current trees qualify; source deletion must not hide retained audio.
        var documents = context.DocumentFiles.AsNoTracking().Where(file => file.CurrentStructureTreeId != null &&
            context.DocumentStructureNodes.Any(node => node.TreeId == file.CurrentStructureTreeId && node.DocumentFileId == file.Id));
        if (query.After is { } after) documents = documents.Where(item => item.Id.CompareTo(after) > 0);
        if (query.StructureDefinitionId is { } definition) documents = documents.Where(item => item.StructureDefinitionId == definition);
        if (query.Subject is { } subject) documents = documents.Where(item => item.Subject == subject);
        if (query.Grade is { } grade) documents = documents.Where(item => item.Grade == grade);
        var current = documents.Select(file => new { File = file, TreeId = file.CurrentStructureTreeId });
        if (query.HasAudio) current = current.Where(item => context.DocumentStructureAudio.Any(audio =>
            audio.TreeId == item.TreeId && audio.DocumentFileId == item.File.Id && context.DocumentStructureNodes.Any(node =>
                node.Id == audio.NodeId && node.TreeId == audio.TreeId && node.DocumentFileId == audio.DocumentFileId && node.Level!.DefinitionId == item.File.StructureDefinitionId)));
        var rows = await current.OrderBy(item => item.File.Id).Take(query.PageSize + 1)
            .Select(item => new InternalDocument(item.File.Id, item.File.FileName, item.File.Subject, item.File.Grade, item.File.StructureDefinitionId,
                context.StructureDefinitions.Where(definition => definition.Id == item.File.StructureDefinitionId)
                    .Select(definition => definition.Name).FirstOrDefault(),
                context.DocumentStructureAudio.Count(audio => audio.TreeId == item.TreeId && audio.DocumentFileId == item.File.Id &&
                    context.DocumentStructureNodes.Any(node => node.Id == audio.NodeId && node.TreeId == audio.TreeId && node.DocumentFileId == audio.DocumentFileId &&
                        node.Level!.DefinitionId == item.File.StructureDefinitionId))))
            .ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new InternalDocumentPage(true, rows);
    }
}
