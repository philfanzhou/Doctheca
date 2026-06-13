using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocRetrieval.Contract.Protos;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Services;

namespace Ruoyu.Study.DocRetrieval.Service;

public class DocumentRetrievalServiceImpl : DocumentRetrievalService.DocumentRetrievalServiceBase
{
    private readonly ISearchDomainService _searchService;
    private readonly ILogger<DocumentRetrievalServiceImpl> _logger;

    public DocumentRetrievalServiceImpl(
        ISearchDomainService searchService,
        ILogger<DocumentRetrievalServiceImpl> logger)
    {
        _searchService = searchService;
        _logger = logger;
    }

    public override async Task<SearchResponse> ExactSearch(ExactSearchRequest request, ServerCallContext context)
    {
        ValidateSearchRequest(request.Query, request.PageSize);

        var filter = MapFilter(request.Filter);
        var pageSize = request.PageSize > 0 ? Math.Min(request.PageSize, 100) : 50;

        var (results, totalCount, nextToken) = await _searchService.ExactSearchAsync(
            request.Query,
            request.Phrase,
            filter,
            pageSize,
            string.IsNullOrEmpty(request.PageToken) ? null : request.PageToken);

        var response = new SearchResponse
        {
            TotalCount = totalCount,
            NextPageToken = nextToken ?? string.Empty
        };
        response.Results.AddRange(results.Select(MapSearchResult));

        return response;
    }

    private static void ValidateSearchRequest(string query, int pageSize)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "DOCRETRIEVAL_QUERY_REQUIRED: Query cannot be empty"));

        if (query.Length > 200)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "DOCRETRIEVAL_QUERY_TOO_LONG: Query exceeds 200 characters"));

        if (pageSize > 100)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "DOCRETRIEVAL_PAGE_SIZE_INVALID: page_size exceeds maximum value 100"));
    }

    private static SearchFilterModel? MapFilter(SearchFilter? filter)
    {
        if (filter == null) return null;
        return new SearchFilterModel
        {
            DocumentTitle = string.IsNullOrEmpty(filter.DocumentTitle) ? null : filter.DocumentTitle,
            Subject = string.IsNullOrEmpty(filter.Subject) ? null : filter.Subject,
            Grade = string.IsNullOrEmpty(filter.Grade) ? null : filter.Grade,
            Year = string.IsNullOrEmpty(filter.Year) ? null : filter.Year
        };
    }

    private static SearchResult MapSearchResult(SearchResultModel result) => new()
    {
        DocumentName = result.DocumentName,
        PageNumber = result.PageNumber,
        AssociatedText = result.AssociatedText,
        Score = result.Score,
        MatchType = result.MatchType,
        SegmentId = result.SegmentId,
        StartOffset = result.StartOffset,
        EndOffset = result.EndOffset
    };
}
