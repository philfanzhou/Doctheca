using System.Data;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Doctheca.Database.Repositories;

public sealed class SearchPageLabelRepository(DbContextOptions<DocthecaDbContext> options) : ISearchPageLabelRepository
{
    public async Task<IReadOnlyDictionary<SearchBlockPageKey, string?>> ReadBatchAsync(
        IReadOnlyCollection<SearchBlockPageKey> keys, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requested = keys.Where(key => key.BlockId != Guid.Empty && key.PageNumber >= 0 && key.PageNumber < int.MaxValue).ToHashSet();
        if (requested.Count == 0) return new Dictionary<SearchBlockPageKey, string?>();
        var blockIds = requested.Select(key => key.BlockId).Distinct().ToArray();

        // Optional enrichment must not leak provider exceptions through EF diagnostics.
        await using var context = new DocthecaDbContext(new DbContextOptionsBuilder<DocthecaDbContext>(options)
            .UseLoggerFactory(NullLoggerFactory.Instance).EnableSensitiveDataLogging(false).EnableDetailedErrors(false).Options);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        var rows = await (from block in context.DocumentParseBlocks.AsNoTracking()
                          join parse in context.DocumentParses.AsNoTracking() on block.ParseId equals parse.Id
                          join page in context.DocumentParsePages.AsNoTracking()
                              on new { block.ParseId, Number = (long)block.PageId + 1 }
                              equals new { page.ParseId, Number = (long)page.PageNumber } into pages
                          from page in pages.DefaultIfEmpty()
                          where blockIds.Contains(block.Id)
                          select new
                          {
                              block.Id, block.ParseId, block.PageId, block.BlockData, parse.StructaDocParseRunId,
                              Label = page == null ? null : page.Label,
                          }).ToListAsync(cancellationToken);

        var labels = new Dictionary<SearchBlockPageKey, string?>();
        foreach (var row in rows)
        {
            var key = new SearchBlockPageKey(row.Id, row.PageId);
            if (!requested.Contains(key)) continue;
            var parse = new DocumentParseModel { Id = row.ParseId, StructaDocParseRunId = row.StructaDocParseRunId };
            var block = new DocumentParseBlockModel { ParseId = row.ParseId, PageId = row.PageId, BlockData = row.BlockData };
            if (DocumentParsePageIdentity.FromBlock(parse, block).HasValue)
                labels.Add(key, row.Label);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return labels;
    }
}
