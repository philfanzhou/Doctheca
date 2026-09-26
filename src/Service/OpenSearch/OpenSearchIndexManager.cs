using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenSearch.Net;

namespace Doctheca.Service.OpenSearch;

internal sealed class OpenSearchIndexManager
{
    /// <summary>
    /// Current index mapping version. Increment when BuildIndexBody mapping changes.
    /// v1 = initial V1 search; v2 = minerU Gen-2 fields (x0/y0/x1/y1/score/has_image/sub_type/text_level/text_format/caption/_meta.block_data).
    /// On startup, if the existing index has a missing or lower mapping_version, the index is deleted and recreated.
    /// </summary>
    internal const int CurrentMappingVersion = 2;

    private readonly OpenSearchLowLevelClient _client;
    private readonly string _indexName;
    private readonly ILogger _logger;

    public OpenSearchIndexManager(OpenSearchLowLevelClient client, string indexName, ILogger logger)
    {
        _client = client;
        _indexName = indexName;
        _logger = logger;
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
                _meta = new { mapping_version = CurrentMappingVersion },
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
    /// Ensures the OpenSearch index exists with the current mapping version.
    /// If the index exists but has an outdated mapping_version, it is deleted and recreated.
    /// </summary>
    internal async Task EnsureIndexAsync()
    {
        await EnsureIndexExistsAsync(_client, _indexName, _logger);
    }

    private static async Task EnsureIndexExistsAsync(
        OpenSearchLowLevelClient client, string indexName, ILogger logger)
    {
        var existsResponse = await client.Indices.ExistsAsync<BytesResponse>(indexName);
        if (existsResponse.Success && existsResponse.HttpStatusCode == 200)
        {
            // Index exists — check mapping version to decide whether to rebuild
            var existingVersion = await GetIndexMappingVersionAsync(client, indexName, logger);
            if (existingVersion == CurrentMappingVersion)
            {
                logger.LogInformation("OpenSearch index already exists with current mapping version: {IndexName} (v{Version})",
                    indexName, existingVersion);
                return;
            }

            logger.LogWarning(
                "OpenSearch index {IndexName} has outdated mapping (expected=v{Expected}, actual=v{Actual}), recreating",
                indexName, CurrentMappingVersion, existingVersion.HasValue ? existingVersion.Value.ToString() : "missing");

            // Delete the outdated index (best-effort)
            var deleteResponse = await client.Indices.DeleteAsync<BytesResponse>(indexName);
            if (!deleteResponse.Success || (deleteResponse.HttpStatusCode != 200 && deleteResponse.HttpStatusCode != 404))
            {
                logger.LogWarning("Failed to delete outdated OpenSearch index {IndexName}, status code: {StatusCode} — proceeding without rebuild",
                    indexName, deleteResponse.HttpStatusCode);
                return;
            }

            logger.LogInformation("Outdated OpenSearch index deleted: {IndexName}", indexName);
            // Fall through to create the index with the current mapping
        }

        var json = JsonSerializer.Serialize(BuildIndexBody());
        var response = await client.Indices.CreateAsync<BytesResponse>(indexName, json);

        if (response.Success && (response.HttpStatusCode == 200 || response.HttpStatusCode == 201))
        {
            logger.LogInformation("OpenSearch index created: {IndexName} (mapping v{Version})", indexName, CurrentMappingVersion);
        }
        else
        {
            var errorBody = response.Body != null ? Encoding.UTF8.GetString(response.Body) : "(empty)";
            logger.LogWarning("OpenSearch index creation failed: {IndexName}, status code: {StatusCode}, response: {Response}",
                indexName, response.HttpStatusCode, errorBody);
        }
    }

    /// <summary>
    /// Reads the index _meta.mapping_version. Returns null if the index or the version field is missing.
    /// </summary>
    private static async Task<int?> GetIndexMappingVersionAsync(
        OpenSearchLowLevelClient client, string indexName, ILogger logger)
    {
        try
        {
            var mappingResponse = await client.Indices.GetMappingAsync<BytesResponse>(indexName);
            if (!mappingResponse.Success || mappingResponse.HttpStatusCode != 200 || mappingResponse.Body == null)
            {
                logger.LogWarning("Failed to read OpenSearch index mapping for {IndexName}, status code: {StatusCode} — assuming outdated",
                    indexName, mappingResponse.HttpStatusCode);
                return null;
            }

            var mappingJson = Encoding.UTF8.GetString(mappingResponse.Body);
            using var doc = JsonDocument.Parse(mappingJson);
            // OpenSearch GET /{index}/_mapping returns { "{indexName}": { "mappings": { "_meta": { "mapping_version": N } } } }
            if (doc.RootElement.TryGetProperty(indexName, out var indexEl)
                && indexEl.TryGetProperty("mappings", out var mappingsEl)
                && mappingsEl.TryGetProperty("_meta", out var metaEl)
                && metaEl.TryGetProperty("mapping_version", out var versionEl)
                && versionEl.ValueKind == JsonValueKind.Number)
            {
                return versionEl.GetInt32();
            }

            // No mapping_version — index predates the version mechanism
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error reading OpenSearch index mapping version for {IndexName} — assuming outdated", indexName);
            return null;
        }
    }
}
