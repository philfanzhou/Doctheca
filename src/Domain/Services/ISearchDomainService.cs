using System.Collections.Generic;
using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Domain.Services;

public interface ISearchDomainService
{
    Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken);

    Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> HybridSearchAsync(
        string query, bool phrase, int exactTopK, int semanticTopK,
        SearchFilterModel? filter, int pageSize, string? pageToken);
}
