using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocLibrary.Contract.Protos;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Services;

namespace Ruoyu.Study.DocLibrary.Service;

public class DocumentLibraryServiceImpl : DocumentLibraryService.DocumentLibraryServiceBase
{
    private readonly ISearchDomainService _searchService;
    private readonly ILogger<DocumentLibraryServiceImpl> _logger;

    public DocumentLibraryServiceImpl(
        ISearchDomainService searchService,
        ILogger<DocumentLibraryServiceImpl> logger)
    {
        _searchService = searchService;
        _logger = logger;
    }

    // ExactSearch: name constrained by proto definition, no Async suffix per gRPC convention
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
            throw new RpcException(new Status(StatusCode.InvalidArgument, "DOCLIBRARY_QUERY_REQUIRED: Query cannot be empty"));

        if (query.Length > 200)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "DOCLIBRARY_QUERY_TOO_LONG: Query exceeds 200 characters"));

        if (pageSize > 100)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "DOCLIBRARY_PAGE_SIZE_INVALID: page_size exceeds maximum value 100"));
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
        EndOffset = result.EndOffset,
        CreatedAtUnixSeconds = result.CreatedAt?.ToUnixTimeSeconds() ?? 0
    };
}
