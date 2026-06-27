using System.Collections.Generic;
using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Domain.Services;

public interface ISearchDomainService
{
    Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken);

}
