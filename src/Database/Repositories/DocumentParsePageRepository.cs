using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Doctheca.Database.Repositories;

public sealed class DocumentParsePageRepository(DocthecaDbContext context) : IDocumentParsePageRepository
{
    public async Task<IReadOnlyDictionary<DocumentParsePageKey, DocumentParsePage>> ReadBatchAsync(
        IReadOnlyCollection<DocumentParsePageKey> keys, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (keys.Any(key => key.PageNumber <= 0))
            throw new ArgumentException("Physical page numbers must be positive.", nameof(keys));
        if (keys.Count == 0)
            return new Dictionary<DocumentParsePageKey, DocumentParsePage>();

        var requested = keys.ToHashSet();
        var parseIds = requested.Select(key => key.ParseId).Distinct().ToArray();
        var numbers = requested.Select(key => key.PageNumber).Distinct().ToArray();
        var rows = await context.DocumentParsePages.AsNoTracking()
            .Where(page => parseIds.Contains(page.ParseId) && numbers.Contains(page.PageNumber))
            .Select(page => new DocumentParsePage(page.ParseId, page.PageNumber, page.Label, page.LabelSource))
            .ToListAsync(ct);
        ct.ThrowIfCancellationRequested();
        // The SQL bounds the candidates; preserve exact pairs, never a cartesian association.
        return rows.Where(page => requested.Contains(new(page.ParseId, page.PageNumber)))
            .ToDictionary(page => new DocumentParsePageKey(page.ParseId, page.PageNumber));
    }
}
