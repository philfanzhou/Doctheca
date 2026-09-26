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
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Service.OpenSearch;

namespace Doctheca.Service;

public sealed class OpenSearchIndexService : ISearchIndexService
{
    private readonly OpenSearchLowLevelClient _client;
    private readonly OpenSearchOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OpenSearchIndexService> _logger;
    private readonly OpenSearchIndexManager _indexManager;

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

        _indexManager = new OpenSearchIndexManager(_client, _options.IndexName, logger);
    }

    public async Task EnsureIndexAsync()
    {
        await _indexManager.EnsureIndexAsync();
    }

    /// <summary>
    /// Builds the OpenSearch index body (settings + mappings) for index creation.
    /// Forwarded to <see cref="OpenSearchIndexManager"/> for test compatibility.
    /// </summary>
    internal static object BuildIndexBody()
    {
        return OpenSearchIndexManager.BuildIndexBody();
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

        var searchBody = OpenSearchQueryBuilder.BuildSearchBody(query, phrase, filter, pageSize, pageToken, _logger);
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
        return OpenSearchResponseParser.ParseSearchResponse(responseJson, phrase, pageSize);
    }

    /// <summary>
    /// Builds the OpenSearch search request body (pure logic, testable).
    /// Forwarded to <see cref="OpenSearchQueryBuilder"/> for test compatibility.
    /// </summary>
    internal static Dictionary<string, object> BuildSearchBody(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken, ILogger? logger = null)
    {
        return OpenSearchQueryBuilder.BuildSearchBody(query, phrase, filter, pageSize, pageToken, logger);
    }

    /// <summary>
    /// Parses the OpenSearch search response JSON (pure logic, testable).
    /// Forwarded to <see cref="OpenSearchResponseParser"/> for test compatibility.
    /// </summary>
    internal static (List<SearchResultModel> Results, int TotalCount, string? NextToken) ParseSearchResponse(
        string responseJson, bool phrase, int pageSize)
    {
        return OpenSearchResponseParser.ParseSearchResponse(responseJson, phrase, pageSize);
    }
}
