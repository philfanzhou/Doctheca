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

        // 从 URL 解析 host 和 port
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
                _logger.LogInformation("Qdrant 集合已存在：{CollectionName}", collectionName);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "检查 Qdrant 集合列表失败，尝试直接创建");
        }

        // 创建集合，向量维度 1024，距离度量 Cosine
        await _client.CreateCollectionAsync(
            collectionName,
            vectorsConfig: new VectorParams { Size = 1024, Distance = Distance.Cosine });

        _logger.LogInformation("Qdrant 集合已创建：{CollectionName}", collectionName);

        // 创建 payload 索引
        try
        {
            await _client.CreatePayloadIndexAsync(collectionName, "document_id", PayloadSchemaType.Keyword);
            await _client.CreatePayloadIndexAsync(collectionName, "subject", PayloadSchemaType.Keyword);
            await _client.CreatePayloadIndexAsync(collectionName, "grade", PayloadSchemaType.Keyword);
            await _client.CreatePayloadIndexAsync(collectionName, "year", PayloadSchemaType.Keyword);
            await _client.CreatePayloadIndexAsync(collectionName, "segment_type", PayloadSchemaType.Keyword);
            _logger.LogInformation("Qdrant payload 索引已创建");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "创建 Qdrant payload 索引失败（可能已存在）");
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

        // 收集所有需要向量化的文本
        var texts = new List<string>();
        var points = new List<PointStruct>();

        // 处理 sentence segments
        var segments = await segmentRepository.GetByDocumentIdAsync(documentId);
        foreach (var seg in segments)
        {
            var pageNumber = pageLookup.TryGetValue(seg.PageId, out var pn) ? pn : 0;
            texts.Add(seg.Text);
            points.Add(new PointStruct
            {
                Id = new PointId { Uuid = Guid.NewGuid().ToString() },
                Vectors = Array.Empty<float>(), // 占位，后续填入实际向量
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

        // 处理 question segments
        var questions = await questionRepository.GetByDocumentIdAsync(documentId);
        foreach (var q in questions)
        {
            var pageNumber = pageLookup.TryGetValue(q.PageId, out var pn) ? pn : 0;
            texts.Add(q.Stem);
            points.Add(new PointStruct
            {
                Id = new PointId { Uuid = Guid.NewGuid().ToString() },
                Vectors = Array.Empty<float>(), // 占位，后续填入实际向量
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
            _logger.LogInformation("文档 {DocumentId} 没有可向量化的 segments", documentId);
            return;
        }

        // 批量获取 Embedding 向量，每批 16 条
        const int batchSize = 16;
        for (var i = 0; i < texts.Count; i += batchSize)
        {
            var batchTexts = texts.Skip(i).Take(batchSize).ToList();
            var batchPoints = points.Skip(i).Take(batchSize).ToList();

            var embeddings = await GetEmbeddingsWithRetryAsync(batchTexts);

            // 将向量填入 points
            for (var j = 0; j < embeddings.Count; j++)
            {
                batchPoints[j].Vectors = embeddings[j];
            }

            // 写入 Qdrant
            await _client.UpsertAsync(collectionName, batchPoints);
            _logger.LogInformation("文档 {DocumentId} 已写入 Qdrant 批次 {Batch}（{Count} 条）",
                documentId, i / batchSize + 1, batchPoints.Count);
        }

        _logger.LogInformation("文档 {DocumentId} 向量索引完成，共 {Count} 条", documentId, texts.Count);
    }

    public async Task DeleteDocumentVectorsAsync(Guid documentId)
    {
        var collectionName = _options.CollectionName;

        // 按 document_id payload 过滤删除
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
        _logger.LogInformation("文档 {DocumentId} 的 Qdrant 向量数据已删除", documentId);
    }

    public async Task<List<SearchResultModel>> SemanticSearchAsync(string query, int topK, SearchFilterModel? filter)
    {
        var collectionName = _options.CollectionName;

        // 获取 query 的 Embedding 向量
        var queryEmbeddings = await GetEmbeddingsWithRetryAsync(new List<string> { query });
        var queryVector = queryEmbeddings[0];

        // 构建 Qdrant 过滤条件
        var qdrantFilter = BuildQdrantFilter(filter);

        // 执行向量搜索
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
    /// 构建 Qdrant 过滤条件
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
    /// 调用 SiliconFlow Embedding API 获取向量，失败重试 3 次，指数退避
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
                _logger.LogWarning(ex, "Embedding API 调用失败，第 {Attempt} 次重试，等待 {Delay}ms",
                    attempt + 1, delay.TotalMilliseconds);
                await Task.Delay(delay);
            }
        }

        // 最后一次尝试不捕获异常
        return await GetEmbeddingsAsync(texts);
    }

    /// <summary>
    /// 调用 SiliconFlow Embedding API 获取向量
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
