using System.Collections.Generic;
using Doctheca.Domain.Models;

namespace Doctheca.Domain.Services;

public interface ISearchService
{
    Task<(List<SearchResult> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
        string query, bool phrase, SearchFilter? filter, int pageSize, string? pageToken);

}
