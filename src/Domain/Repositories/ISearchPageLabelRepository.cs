using Doctheca.Domain.Models;

namespace Doctheca.Domain.Repositories;

public interface ISearchPageLabelRepository
{
    /// <summary>Resolve trusted block/page identities in one read-only relational snapshot.</summary>
    Task<IReadOnlyDictionary<SearchBlockPageKey, string?>> ReadBatchAsync(
        IReadOnlyCollection<SearchBlockPageKey> keys, CancellationToken cancellationToken = default);
}
