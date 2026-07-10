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
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;

namespace Ruoyu.Study.DocLibrary.Service;

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
        await EnsureIndexExistsAsync(_client, _options.IndexName, _logger);
    }

    /// <summary>
    /// Builds the OpenSearch index body (settings + mappings) for index creation.
    /// Internal for unit testing field presence.
    /// </summary>
    internal static object BuildIndexBody()
    {
        return new
        {
            settings = new
            {
                index = new
                {
                    number_of_shards = 1,
                    number_of_replicas = 0
                },
                analysis = new
                {
                    analyzer = new
                    {
                        english_custom = new
                        {
                            type = "custom",
                            tokenizer = "standard",
                            filter = new[] { "lowercase", "english_stop", "english_stemmer" }
                        },
                        english_phrase = new
                        {
                            type = "custom",
                            tokenizer = "standard",
                            filter = new[] { "lowercase" }
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
                    // MinerU blocks pipeline fields
                    parse_id = new { type = "keyword" },
                    document_file_id = new { type = "keyword" },
                    file_name = new { type = "keyword" },
                    block_id = new { type = "keyword" },
                    block_type = new { type = "keyword" },
                    sort_index = new { type = "integer" },
                    image_id = new { type = "keyword" },
                    subject = new { type = "keyword" },
                    grade = new { type = "keyword" },
                    year = new { type = "keyword" },
                    page_number = new { type = "integer" },
                    text = new
                    {
                        type = "text",
                        analyzer = "english_custom",
                        fields = new
                        {
                            exact = new { type = "text", analyzer = "english_phrase" },
                            keyword = new { type = "keyword", ignore_above = 256 }
                        }
                    },
                    created_at = new { type = "date" },
                    // [Gen-2] minerU block-level structured fields
                    x0 = new { type = "float" },
                    y0 = new { type = "float" },
                    x1 = new { type = "float" },
                    y1 = new { type = "float" },
                    score = new { type = "float" },
                    has_image = new { type = "boolean" },
                    sub_type = new { type = "keyword" },
                    text_level = new { type = "integer" },
                    text_format = new { type = "keyword" },
                    caption = new
                    {
                        type = "text",
                        analyzer = "english_custom",
                        fields = new
                        {
                            keyword = new { type = "keyword", ignore_above = 256 }
                        }
                    },
                    _meta = new
                    {
                        type = "object",
                        enabled = true,
                        dynamic = false,
                        properties = new
                        {
                            block_data = new { type = "object", enabled = false }
                        }
                    }
                }
            }
        };
    }

    /// <summary>
    /// Ensures the OpenSearch index exists, creating it if necessary.
    /// </summary>
    private static async Task EnsureIndexExistsAsync(
        OpenSearchLowLevelClient client, string indexName, ILogger logger)
    {
        var existsResponse = await client.Indices.ExistsAsync<BytesResponse>(indexName);
        if (existsResponse.Success && existsResponse.HttpStatusCode == 200)
        {
            logger.LogInformation("OpenSearch index already exists: {IndexName}", indexName);
            return;
        }

        var json = JsonSerializer.Serialize(BuildIndexBody());
        var response = await client.Indices.CreateAsync<BytesResponse>(indexName, json);

        if (response.Success && (response.HttpStatusCode == 200 || response.HttpStatusCode == 201))
        {
            logger.LogInformation("OpenSearch index created: {IndexName}", indexName);
        }
        else
        {
            logger.LogWarning("OpenSearch index creation failed: {IndexName}, status code: {StatusCode}", indexName, response.HttpStatusCode);
        }
    }

    public async Task IndexParseBlocksAsync(Guid parseId, Guid documentFileId, string fileName, string? subject, string? grade, string? year)
    {
        var indexName = _options.IndexName;

        using var scope = _serviceProvider.CreateScope();
        var blockRepository = scope.ServiceProvider.GetRequiredService<IDocumentParseBlockRepository>();

        var blocks = await blockRepository.GetByParseIdAsync(parseId);
        if (blocks.Count == 0)
        {
            _logger.LogWarning("Parse {ParseId} has no blocks to index", parseId);
            return;
        }

        var bulkOps = new List<object>();
        foreach (var block in blocks)
        {
            // Skip blocks with null/empty text content (nothing to search)
            if (string.IsNullOrWhiteSpace(block.TextContent))
                continue;

            bulkOps.Add(new { index = new { _index = indexName, _id = $"block_{block.Id}" } });
            bulkOps.Add(new
            {
                parse_id = parseId.ToString(),
                document_file_id = documentFileId.ToString(),
                file_name = fileName,
                subject = subject ?? string.Empty,
                grade = grade ?? string.Empty,
                year = year ?? string.Empty,
                page_number = block.PageId,
                block_id = block.Id.ToString(),
                block_type = block.BlockType,
                text = block.TextContent,
                sort_index = block.SortIndex,
                image_id = block.ImageId?.ToString() ?? string.Empty,
                created_at = block.CreatedAt.ToString("o"),
                // [Gen-2] minerU block-level fields (same bulk, same _id — no extra network round-trip)
                x0 = block.BboxX0,
                y0 = block.BboxY0,
                x1 = block.BboxX1,
                y1 = block.BboxY1,
                score = block.MineruScore,
                has_image = block.ImageId != null,
                sub_type = block.SubType ?? string.Empty,
                text_level = block.TextLevel,
                text_format = block.TextFormat,
                caption = block.Caption ?? string.Empty,
                _meta = new { block_data = block.BlockData }
            });
        }

        if (bulkOps.Count == 0)
        {
            _logger.LogWarning("Parse {ParseId} has no indexable blocks (all text_content empty)", parseId);
            return;
        }

        var bulkJson = string.Join("\n", bulkOps.Select(op => JsonSerializer.Serialize(op))) + "\n";
        var response = await _client.BulkAsync<BytesResponse>(PostData.String(bulkJson));

        if (response.Success && (response.HttpStatusCode == 200 || response.HttpStatusCode == 201))
        {
            _logger.LogInformation("Parse {ParseId} indexed {BlockCount} blocks to OpenSearch (FileId={FileId})",
                parseId, bulkOps.Count / 2, documentFileId);
        }
        else
        {
            _logger.LogWarning("Parse {ParseId} indexing failed, status code: {StatusCode}", parseId, response.HttpStatusCode);
        }
    }

    public async Task DeleteParseIndexAsync(Guid parseId)
    {
        var indexName = _options.IndexName;
        var deleteBody = new
        {
            query = new
            {
                term = new { parse_id = parseId.ToString() }
            }
        };

        var json = JsonSerializer.Serialize(deleteBody);
        var response = await _client.DeleteByQueryAsync<BytesResponse>(indexName, json);

        if (response.Success && response.HttpStatusCode == 200)
        {
            _logger.LogInformation("Parse {ParseId} search index deleted", parseId);
        }
        else
        {
            _logger.LogWarning("Failed to delete parse {ParseId} search index, status code: {StatusCode}", parseId, response.HttpStatusCode);
        }
    }

    public async Task DeleteDocumentFileIndexAsync(Guid documentFileId)
    {
        var indexName = _options.IndexName;
        var deleteBody = new
        {
            query = new
            {
                term = new { document_file_id = documentFileId.ToString() }
            }
        };

        var json = JsonSerializer.Serialize(deleteBody);
        var response = await _client.DeleteByQueryAsync<BytesResponse>(indexName, json);

        if (response.Success && response.HttpStatusCode == 200)
        {
            _logger.LogInformation("Document file {FileId} search index deleted", documentFileId);
        }
        else
        {
            _logger.LogWarning("Failed to delete document file {FileId} search index, status code: {StatusCode}", documentFileId, response.HttpStatusCode);
        }
    }

    public async Task UpdateDocumentFileMetadataAsync(Guid documentFileId, string? subject, string? grade, string? year)
    {
        var indexName = _options.IndexName;
        var updateBody = new
        {
            query = new
            {
                term = new { document_file_id = documentFileId.ToString() }
            },
            script = new
            {
                source = "ctx._source.subject = params.subject; ctx._source.grade = params.grade; ctx._source.year = params.year",
                @params = new
                {
                    subject = subject ?? string.Empty,
                    grade = grade ?? string.Empty,
                    year = year ?? string.Empty
                }
            }
        };

        var json = JsonSerializer.Serialize(updateBody);
        var response = await _client.UpdateByQueryAsync<BytesResponse>(indexName, json);

        if (response.Success && response.HttpStatusCode == 200)
        {
            _logger.LogInformation(
                "Document file {FileId} search index metadata updated: Subject={Subject}, Grade={Grade}, Year={Year}",
                documentFileId, subject ?? "(none)", grade ?? "(none)", year ?? "(none)");
        }
        else
        {
            _logger.LogWarning(
                "Failed to update document file {FileId} search index metadata, status code: {StatusCode}",
                documentFileId, response.HttpStatusCode);
        }
    }

    public async Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken)
    {
        var indexName = _options.IndexName;

        var searchBody = BuildSearchBody(query, phrase, filter, pageSize, pageToken, _logger);
        var json = JsonSerializer.Serialize(searchBody);
        _logger.LogDebug("OpenSearch search request: index={Index}, body={Body}", indexName, json);
        var response = await _client.SearchAsync<BytesResponse>(indexName, json);

        if (!response.Success || response.HttpStatusCode != 200)
        {
            var errorBody = response.Body != null ? Encoding.UTF8.GetString(response.Body) : "(empty)";
            throw new InvalidOperationException(
                $"OpenSearch query failed, status code: {response.HttpStatusCode}, response: {errorBody}, query: {json}");
        }

        var responseJson = Encoding.UTF8.GetString(response.Body);
        return ParseSearchResponse(responseJson, phrase, pageSize);
    }

    /// <summary>
    /// Builds the OpenSearch search request body (pure logic, testable).
    /// </summary>
    internal static Dictionary<string, object> BuildSearchBody(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken, ILogger? logger = null)
    {
        // Build the main query
        // Phrase query uses text.exact field (english_phrase analyzer, lowercase only without stemming, ensures phrase integrity)
        // Non-phrase query uses text field (english_custom analyzer, stemming for expanded recall)
        // Note: OpenSearch multi-field uses "text.exact" at query time, C# anonymous objects cannot contain dots in property names, use dictionary instead
        object mainQuery;
        if (phrase)
        {
            mainQuery = new Dictionary<string, object>
            {
                ["match_phrase"] = new Dictionary<string, object> { ["text.exact"] = new { query } }
            };
        }
        else
        {
            mainQuery = new { match = new { text = new { query } } };
        }

        // Build filter clauses
        var filterClauses = new List<object>();
        if (filter != null)
        {
            // V1 filters (unchanged — zero regression)
            if (!string.IsNullOrEmpty(filter.Subject))
                filterClauses.Add(new { term = new { subject = new { value = filter.Subject } } });
            if (!string.IsNullOrEmpty(filter.Grade))
                filterClauses.Add(new { term = new { grade = new { value = filter.Grade } } });
            if (!string.IsNullOrEmpty(filter.Year))
                filterClauses.Add(new { term = new { year = new { value = filter.Year } } });
            // file_name 是 keyword 类型，使用 term 精确匹配
            if (!string.IsNullOrEmpty(filter.DocumentTitle))
                filterClauses.Add(new { term = new { file_name = new { value = filter.DocumentTitle } } });

            // [Gen-2] minerU block-level filters (all term+filter; null → not added → zero regression)
            if (!string.IsNullOrEmpty(filter.BlockType))
                filterClauses.Add(new { term = new { block_type = new { value = filter.BlockType } } });
            if (!string.IsNullOrEmpty(filter.BlockSubType))
                filterClauses.Add(new { term = new { sub_type = new { value = filter.BlockSubType } } });
            if (filter.PageNumber.HasValue)
                filterClauses.Add(new { term = new { page_number = new { value = filter.PageNumber.Value } } });
            if (filter.TextLevel.HasValue)
                filterClauses.Add(new { term = new { text_level = new { value = filter.TextLevel.Value } } });
            if (!string.IsNullOrEmpty(filter.TextFormat))
                filterClauses.Add(new { term = new { text_format = new { value = filter.TextFormat } } });
            if (filter.ParseId.HasValue)
                filterClauses.Add(new { term = new { parse_id = new { value = filter.ParseId.Value.ToString() } } });
            if (filter.DocumentFileId.HasValue)
                filterClauses.Add(new { term = new { document_file_id = new { value = filter.DocumentFileId.Value.ToString() } } });
            if (filter.HasImage.HasValue)
                filterClauses.Add(new { term = new { has_image = new { value = filter.HasImage.Value } } });
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
            catch (Exception ex)
            {
                // pageToken is opaque client input; invalid tokens are expected occasionally
                logger?.LogDebug(ex, "Failed to decode page token, starting from first page");
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
                new { block_id = new { order = "asc" } }
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

        return searchBody;
    }

    /// <summary>
    /// Parses the OpenSearch search response JSON (pure logic, testable).
    /// </summary>
    internal static (List<SearchResultModel> Results, int TotalCount, string? NextToken) ParseSearchResponse(
        string responseJson, bool phrase, int pageSize)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var hasHits = root.TryGetProperty("hits", out var hitsEl);
        var totalCount = hasHits
            && hitsEl.TryGetProperty("total", out var totalEl)
            && totalEl.TryGetProperty("value", out var valueEl)
            ? valueEl.GetInt32()
            : 0;

        var results = new List<SearchResultModel>();
        JsonElement lastSort = default;
        var hasLastSort = false;

        if (hasHits && hitsEl.TryGetProperty("hits", out var hitArray))
        {
            foreach (var hit in hitArray.EnumerateArray())
            {
                var source = hit.GetProperty("_source");

                var segmentId = source.TryGetProperty("block_id", out var bidEl) ? bidEl.GetString() ?? "" : "";

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

                var documentName = source.TryGetProperty("file_name", out var fnEl) ? fnEl.GetString() ?? "" : "";

                // [Gen-2] minerU block-level fields from _source (null/defaults when absent — robust)
                string? blockData = null;
                if (source.TryGetProperty("_meta", out var metaEl)
                    && metaEl.ValueKind == JsonValueKind.Object
                    && metaEl.TryGetProperty("block_data", out var bdEl)
                    && bdEl.ValueKind == JsonValueKind.String)
                {
                    blockData = bdEl.GetString();
                }

                float[]? bbox = null;
                var x0 = TryGetFloat(source, "x0");
                var y0 = TryGetFloat(source, "y0");
                var x1 = TryGetFloat(source, "x1");
                var y1 = TryGetFloat(source, "y1");
                if (x0.HasValue || y0.HasValue || x1.HasValue || y1.HasValue)
                {
                    bbox = new float[4];
                    bbox[0] = x0 ?? 0f;
                    bbox[1] = y0 ?? 0f;
                    bbox[2] = x1 ?? 0f;
                    bbox[3] = y1 ?? 0f;
                }

                var mineruScore = TryGetDouble(source, "score");
                var subType = TryGetString(source, "sub_type");
                var textLevel = TryGetInt(source, "text_level");
                var textFormat = TryGetString(source, "text_format");
                var caption = TryGetString(source, "caption");

                results.Add(new SearchResultModel
                {
                    DocumentName = documentName,
                    PageNumber = source.TryGetProperty("page_number", out var pnEl) ? pnEl.GetInt32() : 0,
                    AssociatedText = associatedText,
                    Score = score,
                    MatchType = phrase ? SearchMatchType.ExactPhrase : SearchMatchType.Stemmed,
                    SegmentId = segmentId,
                    StartOffset = 0,
                    EndOffset = 0,
                    CreatedAt = source.TryGetProperty("created_at", out var caEl) && DateTimeOffset.TryParse(caEl.GetString(), out var ca) ? ca : null,
                    // [Gen-2] minerU fields
                    BlockData = blockData,
                    Bbox = bbox,
                    MineruScore = mineruScore,
                    SubType = subType,
                    TextLevel = textLevel,
                    TextFormat = textFormat,
                    Caption = caption
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

    // [Gen-2] Safe extraction helpers for nullable _source fields (null when absent or wrong type)

    private static float? TryGetFloat(JsonElement source, string fieldName)
    {
        if (source.TryGetProperty(fieldName, out var el) && el.ValueKind == JsonValueKind.Number)
            return el.GetSingle();
        return null;
    }

    private static double? TryGetDouble(JsonElement source, string fieldName)
    {
        if (source.TryGetProperty(fieldName, out var el) && el.ValueKind == JsonValueKind.Number)
            return el.GetDouble();
        return null;
    }

    private static string? TryGetString(JsonElement source, string fieldName)
    {
        if (source.TryGetProperty(fieldName, out var el) && el.ValueKind == JsonValueKind.String)
        {
            var v = el.GetString();
            return string.IsNullOrEmpty(v) ? null : v;
        }
        return null;
    }

    private static int? TryGetInt(JsonElement source, string fieldName)
    {
        if (source.TryGetProperty(fieldName, out var el) && el.ValueKind == JsonValueKind.Number)
            return el.GetInt32();
        return null;
    }

}
