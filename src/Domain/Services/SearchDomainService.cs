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
        ISearchIndexService? searchIndexService,
        IDocumentRepository documentRepository,
        IDocumentSegmentRepository segmentRepository,
        IQuestionSegmentRepository questionRepository,
        IDocumentPageRepository pageRepository,
        ILogger<SearchDomainService> logger)
    {
        _searchIndexService = searchIndexService;
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
                var (results, totalCount, nextToken) = await _searchIndexService.ExactSearchAsync(query, phrase, filter, pageSize, pageToken);
                if (results.Count > 0)
                    return (results, totalCount, nextToken);

                _logger.LogInformation("OpenSearch returned 0 results for query '{Query}', falling back to database search", query);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenSearch query failed, falling back to database search");
            }
        }

        // Fallback: database search
        return await DatabaseSearchAsync(query, phrase, filter, pageSize, pageToken);
    }

    private async Task<(List<SearchResultModel>, int, string?)> DatabaseSearchAsync(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken)
    {
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

        var subject = filter?.Subject;
        var grade = filter?.Grade;
        var year = filter?.Year;

        // Query segments and questions with database-level LIKE + pagination
        var (segments, segmentTotal) = await _segmentRepository.SearchByTextAsync(query, pageSize, skip, DocumentStatus.Ready, subject, grade, year);
        var (questions, questionTotal) = await _questionRepository.SearchByStemAsync(query, pageSize, skip, DocumentStatus.Ready, subject, grade, year);

        var results = new List<SearchResultModel>();

        // Build document title lookup (batch by document IDs)
        var documentIds = segments.Select(s => s.DocumentId)
            .Concat(questions.Select(q => q.DocumentId))
            .Distinct()
            .ToList();

        var documentTitles = new Dictionary<Guid, string>();
        var pageNumbers = new Dictionary<Guid, int>(); // PageId -> PageNumber

        foreach (var docId in documentIds)
        {
            var doc = await _documentRepository.GetByIdAsync(docId);
            if (doc != null)
                documentTitles[docId] = doc.Title;

            var pages = await _pageRepository.GetByDocumentIdAsync(docId);
            foreach (var p in pages)
                pageNumbers[p.Id] = p.PageNumber;
        }

        // Map segment results
        foreach (var seg in segments)
        {
            var pageNumber = pageNumbers.TryGetValue(seg.PageId, out var pn) ? pn : 0;
            var docTitle = documentTitles.TryGetValue(seg.DocumentId, out var t) ? t : "";
            results.Add(new SearchResultModel
            {
                DocumentName = docTitle,
                PageNumber = pageNumber,
                AssociatedText = seg.Text,
                Score = phrase ? 1.0 : 0.8,
                MatchType = phrase ? SearchMatchType.ExactPhrase : SearchMatchType.ExactWord,
                SegmentId = seg.SentenceId,
                StartOffset = seg.StartOffset,
                EndOffset = seg.EndOffset,
                CreatedAt = seg.CreatedAt
            });
        }

        // Map question results
        foreach (var q in questions)
        {
            var pageNumber = pageNumbers.TryGetValue(q.PageId, out var pn) ? pn : 0;
            var docTitle = documentTitles.TryGetValue(q.DocumentId, out var t) ? t : "";
            results.Add(new SearchResultModel
            {
                DocumentName = docTitle,
                PageNumber = pageNumber,
                AssociatedText = q.Stem,
                Score = phrase ? 1.0 : 0.8,
                MatchType = phrase ? SearchMatchType.ExactPhrase : SearchMatchType.ExactWord,
                SegmentId = q.QuestionId,
                StartOffset = q.StartOffset,
                EndOffset = q.EndOffset,
                CreatedAt = q.CreatedAt
            });
        }

        // Deduplicate and sort
        results = results
            .GroupBy(r => $"{r.DocumentName}|{r.PageNumber}|{r.SegmentId}")
            .Select(g => g.First())
            .OrderByDescending(r => r.Score)
            .ToList();

        var totalCount = segmentTotal + questionTotal;
        var nextToken = (skip + results.Count) < totalCount
            ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { skip = skip + pageSize })))
            : null;

        return (results, totalCount, nextToken);
    }
}
