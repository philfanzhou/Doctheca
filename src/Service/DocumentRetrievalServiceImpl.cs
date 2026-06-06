using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocRetrieval.Contract.Protos;
using Ruoyu.Study.DocRetrieval.Domain.Services;

namespace Ruoyu.Study.DocRetrieval.Service;

public class DocumentRetrievalServiceImpl : DocumentRetrievalService.DocumentRetrievalServiceBase
{
    private readonly SearchDomainService _searchService;
    private readonly ILogger<DocumentRetrievalServiceImpl> _logger;

    public DocumentRetrievalServiceImpl(
        SearchDomainService searchService,
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

    public override async Task<SearchResponse> HybridSearch(HybridSearchRequest request, ServerCallContext context)
    {
        ValidateSearchRequest(request.Query, request.PageSize);

        var filter = MapFilter(request.Filter);
        var pageSize = request.PageSize > 0 ? Math.Min(request.PageSize, 100) : 50;
        var exactTopK = request.ExactTopK > 0 ? Math.Min(request.ExactTopK, 200) : 50;
        var semanticTopK = request.SemanticTopK > 0 ? Math.Min(request.SemanticTopK, 100) : 20;

        var (results, totalCount, nextToken) = await _searchService.HybridSearchAsync(
            request.Query,
            request.Phrase,
            exactTopK,
            semanticTopK,
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
            throw new RpcException(new Status(StatusCode.InvalidArgument, "查询词不能为空"));

        if (query.Length > 200)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "查询词超过200字符"));

        if (pageSize > 100)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "page_size超过最大值100"));
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
