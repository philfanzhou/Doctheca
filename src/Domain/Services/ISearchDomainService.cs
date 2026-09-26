using System.Collections.Generic;
using Doctheca.Domain.Models;

namespace Doctheca.Domain.Services;

public interface ISearchDomainService
{
    Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken);

}
