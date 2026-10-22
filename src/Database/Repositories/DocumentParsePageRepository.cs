using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Doctheca.Database.Repositories;

public sealed class DocumentParsePageRepository(DocthecaDbContext context) : IDocumentParsePageRepository
{
    public async Task ReplaceAsync(Guid parseId, IReadOnlyCollection<DocumentParsePageModel> pages, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (pages.Any(page => page.ParseId != parseId || page.PageNumber <= 0)
            || pages.Select(page => page.PageNumber).Distinct().Count() != pages.Count)
            throw new ArgumentException("A page replacement must contain unique positive pages owned by its parse.", nameof(pages));

        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        // Serialize all replacements, including initially empty sets, on their owning parse.
        var owner = await context.Database.SqlQuery<Guid>(
            $"SELECT id AS \"Value\" FROM document_parses WHERE id = {parseId} FOR UPDATE").ToListAsync(ct);
        if (owner.Count == 0)
            throw new InvalidOperationException("The page replacement's parse does not exist.");

        await context.DocumentParsePages.Where(page => page.ParseId == parseId).ExecuteDeleteAsync(ct);
        foreach (var page in pages)
        {
            // Parameterized writes avoid leaving tracked inserts after a transaction failure.
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO document_parse_pages (parse_id, page_number, label, label_source)
                VALUES ({parseId}, {page.PageNumber}, {page.Label}, {page.LabelSource})
                """, ct);
        }
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(ct);
        ct.ThrowIfCancellationRequested();
    }

    public async Task<IReadOnlyDictionary<DocumentParsePageKey, DocumentParsePageModel>> ReadBatchAsync(
        IReadOnlyCollection<DocumentParsePageKey> keys, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (keys.Any(key => key.PageNumber <= 0))
            throw new ArgumentException("Physical page numbers must be positive.", nameof(keys));
        if (keys.Count == 0)
            return new Dictionary<DocumentParsePageKey, DocumentParsePageModel>();

        var requested = keys.ToHashSet();
        var parseIds = requested.Select(key => key.ParseId).Distinct().ToArray();
        var numbers = requested.Select(key => key.PageNumber).Distinct().ToArray();
        var rows = await context.DocumentParsePages.AsNoTracking()
            .Where(page => parseIds.Contains(page.ParseId) && numbers.Contains(page.PageNumber))
            .Select(page => new DocumentParsePageModel(page.ParseId, page.PageNumber, page.Label, page.LabelSource))
            .ToListAsync(ct);
        ct.ThrowIfCancellationRequested();
        // The SQL bounds the candidates; preserve exact pairs, never a cartesian association.
        return rows.Where(page => requested.Contains(new(page.ParseId, page.PageNumber)))
            .ToDictionary(page => new DocumentParsePageKey(page.ParseId, page.PageNumber));
    }
}
