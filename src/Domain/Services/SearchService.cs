using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;

namespace Doctheca.Domain.Services;

public class SearchService : ISearchService
{
    private readonly ISearchIndexService _searchIndexService;
    private readonly ILogger<SearchService> _logger;

    public SearchService(
        ISearchIndexService searchIndexService,
        ILogger<SearchService> logger)
    {
        _searchIndexService = searchIndexService;
        _logger = logger;
    }

    public async Task<(List<SearchResult> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
        string query, bool phrase, SearchFilter? filter, int pageSize, string? pageToken)
    {
        try
        {
            return await _searchIndexService.ExactSearchAsync(query, phrase, filter, pageSize, pageToken);
        }
        catch (Exception)
        {
            _logger.LogWarning("OpenSearch query failed; returning empty results");
            return (Results: [], TotalCount: 0, NextToken: null);
        }
    }
}
