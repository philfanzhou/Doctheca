using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;

namespace Ruoyu.Study.DocLibrary.Domain.Services;

public class SearchDomainService : ISearchDomainService
{
    private readonly ISearchIndexService _searchIndexService;
    private readonly ILogger<SearchDomainService> _logger;

    public SearchDomainService(
        ISearchIndexService searchIndexService,
        ILogger<SearchDomainService> logger)
    {
        _searchIndexService = searchIndexService;
        _logger = logger;
    }

    public async Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken)
    {
        try
        {
            return await _searchIndexService.ExactSearchAsync(query, phrase, filter, pageSize, pageToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenSearch query failed for query '{Query}'", query);
            throw;
        }
    }
}
