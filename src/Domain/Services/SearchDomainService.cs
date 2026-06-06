using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;

namespace Ruoyu.Study.DocRetrieval.Domain.Services;

public class SearchFilterModel
{
    public string? DocumentTitle { get; set; }
    public string? Subject { get; set; }
    public string? Grade { get; set; }
    public string? Year { get; set; }
}

public class SearchResultModel
{
    public string DocumentName { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public string AssociatedText { get; set; } = string.Empty;
    public double Score { get; set; }
    public string MatchType { get; set; } = string.Empty;
    public string SegmentId { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
}

public class SearchDomainService
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentSegmentRepository _segmentRepository;
    private readonly IQuestionSegmentRepository _questionRepository;
    private readonly ILogger<SearchDomainService> _logger;

    public SearchDomainService(
        IDocumentRepository documentRepository,
        IDocumentSegmentRepository segmentRepository,
        IQuestionSegmentRepository questionRepository,
        ILogger<SearchDomainService> logger)
    {
        _documentRepository = documentRepository;
        _segmentRepository = segmentRepository;
        _questionRepository = questionRepository;
        _logger = logger;
    }

    public async Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken)
    {
        // 当前实现：基于数据库的简单文本搜索
        // 后续阶段将接入 OpenSearch 实现完整精确检索
        var results = new List<SearchResultModel>();
        var documents = await GetFilteredDocumentsAsync(filter);

        foreach (var doc in documents.Where(d => d.Status == "ready"))
        {
            var segments = await _segmentRepository.GetByDocumentIdAsync(doc.Id);
            foreach (var seg in segments)
            {
                var matchIndex = seg.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (matchIndex >= 0)
                {
                    results.Add(new SearchResultModel
                    {
                        DocumentName = doc.Title,
                        PageNumber = 0,
                        AssociatedText = seg.Text,
                        Score = 1.0,
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
                var matchIndex = q.Stem.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (matchIndex >= 0)
                {
                    results.Add(new SearchResultModel
                    {
                        DocumentName = doc.Title,
                        PageNumber = 0,
                        AssociatedText = q.Stem,
                        Score = 1.0,
                        MatchType = phrase ? "exact_phrase" : "exact_word",
                        SegmentId = q.QuestionId,
                        StartOffset = matchIndex,
                        EndOffset = matchIndex + query.Length
                    });
                }
            }
        }

        var totalCount = results.Count;
        var pagedResults = results
            .OrderByDescending(r => r.Score)
            .Take(pageSize)
            .ToList();

        var nextToken = pagedResults.Count < totalCount
            ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{pagedResults.Count}"))
            : null;

        return (pagedResults, totalCount, nextToken);
    }

    public async Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> HybridSearchAsync(
        string query, bool phrase, int exactTopK, int semanticTopK,
        SearchFilterModel? filter, int pageSize, string? pageToken)
    {
        // 当前实现：仅使用精确检索
        // 后续阶段将接入 Qdrant 实现语义召回 + 混合去重排序
        var (results, totalCount, nextToken) = await ExactSearchAsync(query, phrase, filter, pageSize, pageToken);

        // 标记为混合检索结果
        foreach (var r in results)
        {
            if (r.MatchType == "exact_word") r.MatchType = "stem_match";
        }

        return (results, totalCount, nextToken);
    }

    private async Task<List<DocumentModel>> GetFilteredDocumentsAsync(SearchFilterModel? filter)
    {
        if (filter == null)
        {
            var (items, _) = await _documentRepository.GetListAsync(1, 1000);
            return items;
        }

        var (filteredItems, _) = await _documentRepository.GetListAsync(
            1, 1000,
            status: null,
            subject: filter.Subject,
            grade: filter.Grade);

        if (!string.IsNullOrEmpty(filter.DocumentTitle))
            filteredItems = filteredItems.Where(d => d.Title == filter.DocumentTitle).ToList();

        if (!string.IsNullOrEmpty(filter.Year))
            filteredItems = filteredItems.Where(d => d.Year == filter.Year).ToList();

        return filteredItems;
    }
}
