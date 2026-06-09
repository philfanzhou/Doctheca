using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;

namespace Ruoyu.Study.DocRetrieval.Domain.Services;

public class SearchDomainService : ISearchDomainService
{
    private readonly ISearchIndexService? _searchIndexService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentSegmentRepository _segmentRepository;
    private readonly IQuestionSegmentRepository _questionRepository;
    private readonly IDocumentPageRepository _pageRepository;
    private readonly ILogger<SearchDomainService> _logger;

    public SearchDomainService(
        IServiceProvider serviceProvider,
        IDocumentRepository documentRepository,
        IDocumentSegmentRepository segmentRepository,
        IQuestionSegmentRepository questionRepository,
        IDocumentPageRepository pageRepository,
        ILogger<SearchDomainService> logger)
    {
        _searchIndexService = serviceProvider.GetService(typeof(ISearchIndexService)) as ISearchIndexService;
        _documentRepository = documentRepository;
        _segmentRepository = segmentRepository;
        _questionRepository = questionRepository;
        _pageRepository = pageRepository;
        _logger = logger;
    }

    public async Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken)
    {
        // Try OpenSearch first
        if (_searchIndexService != null)
        {
            try
            {
                return await _searchIndexService.ExactSearchAsync(query, phrase, filter, pageSize, pageToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenSearch 查询失败，回退到数据库搜索");
            }
        }

        // Fallback: database search
        return await DatabaseSearchAsync(query, phrase, filter, pageSize, pageToken);
    }

    public async Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> HybridSearchAsync(
        string query, bool phrase, int exactTopK, int semanticTopK,
        SearchFilterModel? filter, int pageSize, string? pageToken)
    {
        if (_searchIndexService != null)
        {
            try
            {
                return await _searchIndexService.HybridSearchAsync(query, phrase, exactTopK, semanticTopK, filter, pageSize, pageToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenSearch 混合查询失败，回退到数据库搜索");
            }
        }

        var (results, totalCount, nextToken) = await DatabaseSearchAsync(query, phrase, filter, pageSize, pageToken);
        foreach (var r in results)
        {
            if (r.MatchType == "exact_word") r.MatchType = "stem_match";
        }
        return (results, totalCount, nextToken);
    }

    private async Task<(List<SearchResultModel>, int, string?)> DatabaseSearchAsync(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken)
    {
        var results = new List<SearchResultModel>();
        var documents = await GetFilteredDocumentsAsync(filter);

        foreach (var doc in documents.Where(d => d.Status == "ready"))
        {
            var pages = await _pageRepository.GetByDocumentIdAsync(doc.Id);
            var pageLookup = pages.ToDictionary(p => p.Id, p => p.PageNumber);

            var segments = await _segmentRepository.GetByDocumentIdAsync(doc.Id);
            foreach (var seg in segments)
            {
                bool matched;
                int matchIndex;
                if (phrase)
                {
                    // 短语查询：必须完整包含整个短语（不拆碎）
                    matchIndex = seg.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    matched = matchIndex >= 0;
                }
                else
                {
                    // 单词查询：包含即可
                    matchIndex = seg.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    matched = matchIndex >= 0;
                }

                if (matched)
                {
                    var pageNumber = pageLookup.TryGetValue(seg.PageId, out var pn) ? pn : 0;
                    results.Add(new SearchResultModel
                    {
                        DocumentName = doc.Title,
                        PageNumber = pageNumber,
                        AssociatedText = seg.Text,
                        Score = phrase ? 1.0 : 0.8,
                        MatchType = phrase ? "exact_phrase" : "exact_word",
                        SegmentId = seg.SentenceId,
                        StartOffset = matchIndex,
                        EndOffset = matchIndex + query.Length
                    });
                }
            }

            var questions = await _questionRepository.GetByDocumentIdAsync(doc.Id);
            foreach (var q in questions)
            {
                bool matched;
                int matchIndex;
                if (phrase)
                {
                    matchIndex = q.Stem.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    matched = matchIndex >= 0;
                }
                else
                {
                    matchIndex = q.Stem.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    matched = matchIndex >= 0;
                }

                if (matched)
                {
                    var pageNumber = pageLookup.TryGetValue(q.PageId, out var pn) ? pn : 0;
                    results.Add(new SearchResultModel
                    {
                        DocumentName = doc.Title,
                        PageNumber = pageNumber,
                        AssociatedText = q.Stem,
                        Score = phrase ? 1.0 : 0.8,
                        MatchType = phrase ? "exact_phrase" : "exact_word",
                        SegmentId = q.QuestionId,
                        StartOffset = matchIndex,
                        EndOffset = matchIndex + query.Length
                    });
                }
            }
        }

        results = results
            .GroupBy(r => $"{r.DocumentName}|{r.PageNumber}|{r.SegmentId}")
            .Select(g => g.First())
            .OrderByDescending(r => r.Score)
            .ToList();

        var totalCount = results.Count;
        var skip = 0;
        if (!string.IsNullOrEmpty(pageToken))
        {
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(pageToken));
                var tokenData = JsonSerializer.Deserialize<Dictionary<string, int>>(decoded);
                skip = tokenData?.GetValueOrDefault("skip", 0) ?? 0;
            }
            catch { skip = 0; }
        }

        var pagedResults = results.Skip(skip).Take(pageSize).ToList();
        var nextToken = (skip + pagedResults.Count) < totalCount
            ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { skip = skip + pagedResults.Count })))
            : null;

        return (pagedResults, totalCount, nextToken);
    }

    private async Task<List<DocumentModel>> GetFilteredDocumentsAsync(SearchFilterModel? filter)
    {
        var allDocs = new List<DocumentModel>();
        var currentPage = 1;
        const int batchSize = 500;
        int fetched;

        do
        {
            var (items, totalCount) = filter == null
                ? await _documentRepository.GetListAsync(currentPage, batchSize)
                : await _documentRepository.GetListAsync(
                    currentPage, batchSize, status: null, subject: filter.Subject,
                    grade: filter.Grade, keyword: filter.DocumentTitle, year: filter.Year);

            allDocs.AddRange(items);
            fetched = items.Count;
            currentPage++;
        } while (fetched == batchSize);

        return allDocs;
    }
}
