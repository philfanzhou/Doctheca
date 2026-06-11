using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;

namespace Ruoyu.Study.DocRetrieval.Service;

public class QdrantService : IQdrantService
{
    private readonly QdrantClient _client;
    private readonly QdrantOptions _options;
    private readonly EmbeddingOptions _embeddingOptions;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<QdrantService> _logger;
    private readonly HttpClient _httpClient;

    public QdrantService(
        IOptions<QdrantOptions> qdrantOptions,
        IOptions<EmbeddingOptions> embeddingOptions,
        IServiceProvider serviceProvider,
        ILogger<QdrantService> logger)
    {
        _options = qdrantOptions.Value;
        _embeddingOptions = embeddingOptions.Value;
        _serviceProvider = serviceProvider;
        _logger = logger;

        // Parse host and port from URL
        var uri = new Uri(_options.Url);
        var host = uri.Host;
        var port = uri.Port;
        _client = new QdrantClient(host, port, https: uri.Scheme == "https", apiKey: null);

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task EnsureCollectionAsync()
    {
        var collectionName = _options.CollectionName;

        try
        {
            var collections = await _client.ListCollectionsAsync();
            if (collections.Any(c => c == collectionName))
            {
                _logger.LogInformation("Qdrant collection already exists: {CollectionName}", collectionName);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list Qdrant collections, attempting to create directly");
        }

        // Create collection, vector dimension 1024, distance metric Cosine, on_disk storage for memory optimization
        await _client.CreateCollectionAsync(
            collectionName,
            vectorsConfig: new VectorParams { Size = 1024, Distance = Distance.Cosine, OnDisk = true },
            optimizersConfig: new OptimizersConfigDiff { IndexingThreshold = 20000 });

        _logger.LogInformation("Qdrant collection created: {CollectionName}", collectionName);

        // Create payload indexes
        try
        {
            await _client.CreatePayloadIndexAsync(collectionName, "document_id", PayloadSchemaType.Keyword);
            await _client.CreatePayloadIndexAsync(collectionName, "subject", PayloadSchemaType.Keyword);
            await _client.CreatePayloadIndexAsync(collectionName, "grade", PayloadSchemaType.Keyword);
            await _client.CreatePayloadIndexAsync(collectionName, "year", PayloadSchemaType.Keyword);
            await _client.CreatePayloadIndexAsync(collectionName, "segment_type", PayloadSchemaType.Keyword);
            _logger.LogInformation("Qdrant payload indexes created");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create Qdrant payload indexes (may already exist)");
        }
    }

    public async Task IndexDocumentVectorsAsync(Guid documentId, string documentTitle, string subject, string grade, string year)
    {
        var collectionName = _options.CollectionName;

        using var scope = _serviceProvider.CreateScope();
        var segmentRepository = scope.ServiceProvider.GetRequiredService<IDocumentSegmentRepository>();
        var questionRepository = scope.ServiceProvider.GetRequiredService<IQuestionSegmentRepository>();
        var pageRepository = scope.ServiceProvider.GetRequiredService<IDocumentPageRepository>();

        var pages = await pageRepository.GetByDocumentIdAsync(documentId);
        var pageLookup = pages.ToDictionary(p => p.Id, p => p.PageNumber);

        // Collect all texts to be vectorized
        var texts = new List<string>();
        var points = new List<PointStruct>();

        // Process sentence segments
        var segments = await segmentRepository.GetByDocumentIdAsync(documentId);
        foreach (var seg in segments)
        {
            var pageNumber = pageLookup.TryGetValue(seg.PageId, out var pn) ? pn : 0;
            texts.Add(seg.Text);
            points.Add(new PointStruct
            {
                Id = new PointId { Uuid = Guid.NewGuid().ToString() },
                Vectors = Array.Empty<float>(), // Placeholder, actual vectors filled in later
                Payload =
                {
                    ["document_id"] = documentId.ToString(),
                    ["document_title"] = documentTitle,
                    ["subject"] = subject,
                    ["grade"] = grade,
                    ["year"] = year,
                    ["page_number"] = pageNumber,
                    ["block_id"] = seg.BlockId,
                    ["sentence_id"] = seg.SentenceId,
                    ["segment_type"] = "sentence",
                    ["text"] = seg.Text,
                    ["start_offset"] = seg.StartOffset,
                    ["end_offset"] = seg.EndOffset
                }
            });
        }

        // Process question segments
        var questions = await questionRepository.GetByDocumentIdAsync(documentId);
        foreach (var q in questions)
        {
            var pageNumber = pageLookup.TryGetValue(q.PageId, out var pn) ? pn : 0;
            texts.Add(q.Stem);
            points.Add(new PointStruct
            {
                Id = new PointId { Uuid = Guid.NewGuid().ToString() },
                Vectors = Array.Empty<float>(), // Placeholder, actual vectors filled in later
                Payload =
                {
                    ["document_id"] = documentId.ToString(),
                    ["document_title"] = documentTitle,
                    ["subject"] = subject,
                    ["grade"] = grade,
                    ["year"] = year,
                    ["page_number"] = pageNumber,
                    ["question_id"] = q.QuestionId,
                    ["segment_type"] = "question",
                    ["text"] = q.Stem,
                    ["start_offset"] = q.StartOffset,
                    ["end_offset"] = q.EndOffset
                }
            });
        }

        if (texts.Count == 0)
        {
            _logger.LogInformation("Document {DocumentId} has no vectorizable segments", documentId);
            return;
        }

        // Batch get embedding vectors, 16 per batch
        const int batchSize = 16;
        for (var i = 0; i < texts.Count; i += batchSize)
        {
            var batchTexts = texts.Skip(i).Take(batchSize).ToList();
            var batchPoints = points.Skip(i).Take(batchSize).ToList();

            var embeddings = await GetEmbeddingsWithRetryAsync(batchTexts);

            // Fill vectors into points
            for (var j = 0; j < embeddings.Count; j++)
            {
                batchPoints[j].Vectors = embeddings[j];
            }

            // Write to Qdrant
            await _client.UpsertAsync(collectionName, batchPoints);
            _logger.LogInformation("Document {DocumentId} written to Qdrant batch {Batch} ({Count} records)",
                documentId, i / batchSize + 1, batchPoints.Count);
        }

        _logger.LogInformation("Document {DocumentId} vector indexing completed, {Count} records total", documentId, texts.Count);
    }

    public async Task DeleteDocumentVectorsAsync(Guid documentId)
    {
        var collectionName = _options.CollectionName;

        // Delete by document_id payload filter
        var filter = new Filter
        {
            Must =
            {
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "document_id",
                        Match = new Match { Keyword = documentId.ToString() }
                    }
                }
            }
        };

        await _client.DeleteAsync(collectionName, filter);
        _logger.LogInformation("Document {DocumentId} Qdrant vector data deleted", documentId);
    }

    public async Task UpdateDocumentMetadataAsync(Guid documentId, string subject, string grade, string year)
    {
        var collectionName = _options.CollectionName;

        // Filter by document_id, update payload metadata for all matching points
        var filter = new Filter
        {
            Must =
            {
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "document_id",
                        Match = new Match { Keyword = documentId.ToString() }
                    }
                }
            }
        };

        await _client.SetPayloadAsync(
            collectionName,
            new Dictionary<string, Value>
            {
                ["subject"] = subject,
                ["grade"] = grade,
                ["year"] = year
            },
            filter);

        _logger.LogInformation("Document {DocumentId} Qdrant vector metadata updated", documentId);
    }

    public async Task<List<SearchResultModel>> SemanticSearchAsync(string query, int topK, SearchFilterModel? filter)
    {
        var collectionName = _options.CollectionName;

        // Get embedding vector for query
        var queryEmbeddings = await GetEmbeddingsWithRetryAsync(new List<string> { query });
        var queryVector = queryEmbeddings[0];

        // Build Qdrant filter conditions
        var qdrantFilter = BuildQdrantFilter(filter);

        // Execute vector search
        var searchResults = await _client.SearchAsync(
            collectionName,
            queryVector,
            filter: qdrantFilter,
            limit: (ulong)topK);

        var results = new List<SearchResultModel>();
        foreach (var scoredPoint in searchResults)
        {
            var payload = scoredPoint.Payload;

            var segmentType = payload.TryGetValue("segment_type", out var stVal) ? stVal.StringValue : "sentence";
            var segmentId = segmentType == "question"
                ? (payload.TryGetValue("question_id", out var qiVal) ? qiVal.StringValue : "")
                : (payload.TryGetValue("sentence_id", out var siVal) ? siVal.StringValue : "");

            results.Add(new SearchResultModel
            {
                DocumentName = payload.TryGetValue("document_title", out var dtVal) ? dtVal.StringValue : "",
                PageNumber = payload.TryGetValue("page_number", out var pnVal) ? (int)pnVal.IntegerValue : 0,
                AssociatedText = payload.TryGetValue("text", out var textVal) ? textVal.StringValue : "",
                Score = scoredPoint.Score,
                MatchType = "semantic",
                SegmentId = segmentId,
                StartOffset = payload.TryGetValue("start_offset", out var soVal) ? (int)soVal.IntegerValue : 0,
                EndOffset = payload.TryGetValue("end_offset", out var eoVal) ? (int)eoVal.IntegerValue : 0
            });
        }

        return results;
    }

    /// <summary>
    /// Build Qdrant filter conditions
    /// </summary>
    private Filter? BuildQdrantFilter(SearchFilterModel? filter)
    {
        if (filter == null) return null;

        var conditions = new List<Condition>();

        if (!string.IsNullOrEmpty(filter.Subject))
        {
            conditions.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = "subject",
                    Match = new Match { Keyword = filter.Subject }
                }
            });
        }

        if (!string.IsNullOrEmpty(filter.Grade))
        {
            conditions.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = "grade",
                    Match = new Match { Keyword = filter.Grade }
                }
            });
        }

        if (!string.IsNullOrEmpty(filter.Year))
        {
            conditions.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = "year",
                    Match = new Match { Keyword = filter.Year }
                }
            });
        }

        if (!string.IsNullOrEmpty(filter.DocumentTitle))
        {
            conditions.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = "document_title",
                    Match = new Match { Keyword = filter.DocumentTitle }
                }
            });
        }

        return conditions.Count > 0 ? new Filter { Must = { conditions } } : null;
    }

    /// <summary>
    /// Call SiliconFlow Embedding API to get vectors, retry 3 times on failure with exponential backoff
    /// </summary>
    private async Task<List<float[]>> GetEmbeddingsWithRetryAsync(List<string> texts)
    {
        const int maxRetries = 3;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await GetEmbeddingsAsync(texts);
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning(ex, "Embedding API call failed, retry attempt {Attempt}, waiting {Delay}ms",
                    attempt + 1, delay.TotalMilliseconds);
                await Task.Delay(delay);
            }
        }

        // Last attempt does not catch exceptions
        return await GetEmbeddingsAsync(texts);
    }

    /// <summary>
    /// Call SiliconFlow Embedding API to get vectors
    /// </summary>
    private async Task<List<float[]>> GetEmbeddingsAsync(List<string> texts)
    {
        var requestBody = new
        {
            model = _embeddingOptions.Model,
            input = texts
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, _embeddingOptions.ApiUrl)
        {
            Content = content
        };
        request.Headers.Add("Authorization", $"Bearer {_embeddingOptions.ApiKey}");

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var embeddings = new List<float[]>();
        var dataArray = root.GetProperty("data");

        foreach (var item in dataArray.EnumerateArray())
        {
            var embeddingArray = item.GetProperty("embedding");
            var embedding = new float[embeddingArray.GetArrayLength()];
            var i = 0;
            foreach (var val in embeddingArray.EnumerateArray())
            {
                embedding[i++] = val.GetSingle();
            }
            embeddings.Add(embedding);
        }

        return embeddings;
    }
}
