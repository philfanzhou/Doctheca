using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;

namespace Doctheca.Domain.Services;

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
            _logger.LogWarning(ex, "OpenSearch query failed for query '{Query}', returning empty results", query);
            return (Results: [], TotalCount: 0, NextToken: null);
        }
    }
}
