using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenSearch.Net;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;

namespace Ruoyu.Study.DocRetrieval.Service;

public class OpenSearchIndexService : ISearchIndexService
{
    private readonly OpenSearchLowLevelClient _client;
    private readonly OpenSearchOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OpenSearchIndexService> _logger;

    public OpenSearchIndexService(
        IOptions<OpenSearchOptions> options,
        IServiceProvider serviceProvider,
        ILogger<OpenSearchIndexService> logger)
    {
        _options = options.Value;
        _serviceProvider = serviceProvider;
        _logger = logger;

        var config = new ConnectionConfiguration(new Uri(_options.Url))
            .RequestTimeout(TimeSpan.FromSeconds(30));
        _client = new OpenSearchLowLevelClient(config);
    }

    public async Task EnsureIndexAsync()
    {
        var indexName = _options.IndexName;

        var existsResponse = await _client.Indices.ExistsAsync<BytesResponse>(indexName);
        if (existsResponse.Success && existsResponse.HttpStatusCode == 200)
        {
            _logger.LogInformation("OpenSearch 索引已存在：{IndexName}", indexName);
            return;
        }

        var indexBody = new
        {
            settings = new
            {
                analysis = new
                {
                    analyzer = new
                    {
                        english_custom = new
                        {
                            type = "custom",
                            tokenizer = "standard",
                            filter = new[] { "lowercase", "english_stop", "english_stemmer" }
                        }
                    },
                    filter = new
                    {
                        english_stop = new { type = "stop", stopwords = "_english_" },
                        english_stemmer = new { type = "stemmer", language = "english" }
                    }
                }
            },
            mappings = new
            {
                properties = new
                {
                    document_id = new { type = "keyword" },
                    document_title = new
                    {
                        type = "text",
                        analyzer = "english_custom",
                        fields = new
                        {
                            keyword = new { type = "keyword" }
                        }
                    },
                    subject = new { type = "keyword" },
                    grade = new { type = "keyword" },
                    year = new { type = "keyword" },
                    page_number = new { type = "integer" },
                    block_id = new { type = "keyword" },
                    sentence_id = new { type = "keyword" },
                    question_id = new { type = "keyword" },
                    segment_type = new { type = "keyword" },
                    text = new
                    {
                        type = "text",
                        analyzer = "english_custom",
                        fields = new
                        {
                            exact = new { type = "text", analyzer = "standard" },
                            keyword = new { type = "keyword", ignore_above = 256 }
                        }
                    },
                    start_offset = new { type = "integer" },
                    end_offset = new { type = "integer" },
                    created_at = new { type = "date" }
                }
            }
        };

        var json = JsonSerializer.Serialize(indexBody);
        var response = await _client.Indices.CreateAsync<BytesResponse>(indexName, json);

        if (response.Success && (response.HttpStatusCode == 200 || response.HttpStatusCode == 201))
        {
            _logger.LogInformation("OpenSearch 索引已创建：{IndexName}", indexName);
        }
        else
        {
            _logger.LogWarning("OpenSearch 索引创建失败：{IndexName}，状态码：{StatusCode}", indexName, response.HttpStatusCode);
        }
    }

    public async Task IndexDocumentSegmentsAsync(Guid documentId, string documentTitle, string subject, string grade, string year)
    {
        var indexName = _options.IndexName;

        using var scope = _serviceProvider.CreateScope();
        var segmentRepository = scope.ServiceProvider.GetRequiredService<IDocumentSegmentRepository>();
        var questionRepository = scope.ServiceProvider.GetRequiredService<IQuestionSegmentRepository>();
        var pageRepository = scope.ServiceProvider.GetRequiredService<IDocumentPageRepository>();

        var pages = await pageRepository.GetByDocumentIdAsync(documentId);
        var pageLookup = pages.ToDictionary(p => p.Id, p => p.PageNumber);

        var bulkOps = new List<object>();

        // Index sentence segments
        var segments = await segmentRepository.GetByDocumentIdAsync(documentId);
        foreach (var seg in segments)
        {
            var pageNumber = pageLookup.TryGetValue(seg.PageId, out var pn) ? pn : 0;

            bulkOps.Add(new { index = new { _index = indexName, _id = $"sentence_{seg.SentenceId}" } });
            bulkOps.Add(new
            {
                document_id = documentId.ToString(),
                document_title = documentTitle,
                subject,
                grade,
                year,
                page_number = pageNumber,
                block_id = seg.BlockId,
                sentence_id = seg.SentenceId,
                segment_type = "sentence",
                text = seg.Text,
                start_offset = seg.StartOffset,
                end_offset = seg.EndOffset,
                created_at = seg.CreatedAt.ToString("o")
            });
        }

        // Index question segments
        var questions = await questionRepository.GetByDocumentIdAsync(documentId);
        foreach (var q in questions)
        {
            var pageNumber = pageLookup.TryGetValue(q.PageId, out var pn) ? pn : 0;

            bulkOps.Add(new { index = new { _index = indexName, _id = $"question_{q.QuestionId}" } });
            bulkOps.Add(new
            {
                document_id = documentId.ToString(),
                document_title = documentTitle,
                subject,
                grade,
                year,
                page_number = pageNumber,
                question_id = q.QuestionId,
                segment_type = "question",
                text = q.Stem,
                start_offset = q.StartOffset,
                end_offset = q.EndOffset,
                created_at = q.CreatedAt.ToString("o")
            });
        }

        if (bulkOps.Count == 0)
        {
            _logger.LogInformation("文档 {DocumentId} 没有可索引的segments", documentId);
            return;
        }

        var bulkJson = string.Join("\n", bulkOps.Select(op => JsonSerializer.Serialize(op))) + "\n";
        var response = await _client.BulkAsync<BytesResponse>(bulkJson, indexName);

        if (response.Success && (response.HttpStatusCode == 200 || response.HttpStatusCode == 201))
        {
            _logger.LogInformation("文档 {DocumentId} 已索引 {Count} 个segments", documentId, bulkOps.Count / 2);
        }
        else
        {
            _logger.LogWarning("文档 {DocumentId} 索引失败，状态码：{StatusCode}", documentId, response.HttpStatusCode);
        }
    }

    public async Task DeleteDocumentIndexAsync(Guid documentId)
    {
        var indexName = _options.IndexName;
        var deleteBody = new
        {
            query = new
            {
                term = new { document_id = documentId.ToString() }
            }
        };

        var json = JsonSerializer.Serialize(deleteBody);
        var response = await _client.DeleteByQueryAsync<BytesResponse>(indexName, json);

        if (response.Success && response.HttpStatusCode == 200)
        {
            _logger.LogInformation("文档 {DocumentId} 的搜索索引已删除", documentId);
        }
        else
        {
            _logger.LogWarning("删除文档 {DocumentId} 搜索索引失败，状态码：{StatusCode}", documentId, response.HttpStatusCode);
        }
    }

    public async Task UpdateDocumentMetadataAsync(Guid documentId, string subject, string grade, string year)
    {
        var indexName = _options.IndexName;
        var updateBody = new
        {
            query = new
            {
                term = new { document_id = documentId.ToString() }
            },
            script = new
            {
                source = "ctx._source.subject = params.subject; ctx._source.grade = params.grade; ctx._source.year = params.year",
                @params = new { subject, grade, year }
            }
        };

        var json = JsonSerializer.Serialize(updateBody);
        var response = await _client.UpdateByQueryAsync<BytesResponse>(indexName, json);

        if (response.Success && response.HttpStatusCode == 200)
        {
            _logger.LogInformation("文档 {DocumentId} 的搜索索引元数据已更新", documentId);
        }
        else
        {
            _logger.LogWarning("更新文档 {DocumentId} 搜索索引元数据失败，状态码：{StatusCode}", documentId, response.HttpStatusCode);
        }
    }

    public async Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken)
    {
        var indexName = _options.IndexName;

        // Build the main query
        object mainQuery = phrase
            ? new { match_phrase = new { text = new { query } } }
            : new { match = new { text = new { query } } };

        // Build filter clauses
        var filterClauses = new List<object>();
        if (filter != null)
        {
            if (!string.IsNullOrEmpty(filter.Subject))
                filterClauses.Add(new { term = new { subject = new { value = filter.Subject } } });
            if (!string.IsNullOrEmpty(filter.Grade))
                filterClauses.Add(new { term = new { grade = new { value = filter.Grade } } });
            if (!string.IsNullOrEmpty(filter.Year))
                filterClauses.Add(new { term = new { year = new { value = filter.Year } } });
            if (!string.IsNullOrEmpty(filter.DocumentTitle))
                filterClauses.Add(new { match = new { document_title = new { query = filter.DocumentTitle } } });
        }

        object queryObj = filterClauses.Count > 0
            ? new { @bool = new { must = mainQuery, filter = filterClauses } }
            : mainQuery;

        // Decode page token for search_after
        List<object>? searchAfter = null;
        if (!string.IsNullOrEmpty(pageToken))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(pageToken));
                searchAfter = JsonSerializer.Deserialize<List<object>>(decoded);
            }
            catch
            {
                searchAfter = null;
            }
        }

        var searchBody = new Dictionary<string, object>
        {
            ["size"] = pageSize,
            ["query"] = queryObj,
            ["sort"] = new object[]
            {
                new { _score = new { order = "desc" } },
                new { document_id = new { order = "asc" } },
                new { segment_type = new { order = "asc" } }
            },
            ["highlight"] = new
            {
                fields = new
                {
                    text = new { }
                },
                pre_tags = new[] { "<em>" },
                post_tags = new[] { "</em>" }
            }
        };

        if (searchAfter != null)
        {
            searchBody["search_after"] = searchAfter;
        }

        var json = JsonSerializer.Serialize(searchBody);
        var response = await _client.SearchAsync<BytesResponse>(indexName, json);

        if (!response.Success || response.HttpStatusCode != 200)
        {
            throw new InvalidOperationException($"OpenSearch 查询失败，状态码：{response.HttpStatusCode}");
        }

        var responseJson = Encoding.UTF8.GetString(response.Body);
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var totalCount = root.TryGetProperty("hits", out var hitsEl)
            && hitsEl.TryGetProperty("total", out var totalEl)
            && totalEl.TryGetProperty("value", out var valueEl)
            ? valueEl.GetInt32()
            : 0;

        var results = new List<SearchResultModel>();
        JsonElement lastSort = default;
        var hasLastSort = false;

        if (hitsEl.TryGetProperty("hits", out var hitArray))
        {
            foreach (var hit in hitArray.EnumerateArray())
            {
                var source = hit.GetProperty("_source");
                var segmentType = source.TryGetProperty("segment_type", out var stEl) ? stEl.GetString() ?? "sentence" : "sentence";
                var segmentId = segmentType == "question"
                    ? (source.TryGetProperty("question_id", out var qiEl) ? qiEl.GetString() ?? "" : "")
                    : (source.TryGetProperty("sentence_id", out var siEl) ? siEl.GetString() ?? "" : "");

                var associatedText = source.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";

                // Use highlighted text if available
                if (hit.TryGetProperty("highlight", out var highlightEl)
                    && highlightEl.TryGetProperty("text", out var highlightTexts))
                {
                    var firstHighlight = highlightTexts.EnumerateArray().FirstOrDefault();
                    if (firstHighlight.ValueKind != JsonValueKind.Undefined)
                        associatedText = firstHighlight.GetString() ?? associatedText;
                }

                var score = hit.TryGetProperty("_score", out var scoreEl) ? scoreEl.GetDouble() : 0;

                results.Add(new SearchResultModel
                {
                    DocumentName = source.TryGetProperty("document_title", out var dtEl) ? dtEl.GetString() ?? "" : "",
                    PageNumber = source.TryGetProperty("page_number", out var pnEl) ? pnEl.GetInt32() : 0,
                    AssociatedText = associatedText,
                    Score = score,
                    MatchType = phrase ? "exact_phrase" : "exact_word",
                    SegmentId = segmentId,
                    StartOffset = source.TryGetProperty("start_offset", out var soEl) ? soEl.GetInt32() : 0,
                    EndOffset = source.TryGetProperty("end_offset", out var eoEl) ? eoEl.GetInt32() : 0
                });

                if (hit.TryGetProperty("sort", out var sortEl))
                {
                    lastSort = sortEl;
                    hasLastSort = true;
                }
            }
        }

        string? nextToken = null;
        if (hasLastSort && results.Count == pageSize)
        {
            nextToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(lastSort.GetRawText()));
        }

        return (results, totalCount, nextToken);
    }

    public async Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> HybridSearchAsync(
        string query, bool phrase, int exactTopK, int semanticTopK,
        SearchFilterModel? filter, int pageSize, string? pageToken)
    {
        // Semantic search will be added when embedding service is ready
        return await ExactSearchAsync(query, phrase, filter, pageSize, pageToken);
    }
}
