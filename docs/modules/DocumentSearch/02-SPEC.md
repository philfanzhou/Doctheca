# DocumentSearch — 详细需求规格 (SPEC)

## 功能概述

DocumentSearch 提供两大能力：
1. **OpenSearch 块索引写入**：将 MinerU 解析产生的 `document_parse_blocks` 索引到 OpenSearch，并在解析删除、文件删除、元数据更新时自动维护索引。
2. **精确关键词搜索**：通过 `GET /admin/documents/search` HTTP API 提供基于 OpenSearch BM25 的关键词搜索，支持短语/单词匹配、元数据过滤、游标分页，OpenSearch 不可用时降级为空结果。

## 1. 接口变更

### 1.1 ISearchIndexService（`src/Domain/Repositories/ISearchIndexService.cs`）

```csharp
public interface ISearchIndexService
{
    Task EnsureIndexAsync();
    Task IndexParseBlocksAsync(Guid parseId, Guid documentFileId, string fileName, string? subject, string? grade, string? year);
    Task DeleteParseIndexAsync(Guid parseId);
    Task DeleteDocumentFileIndexAsync(Guid documentFileId);
    Task UpdateDocumentFileMetadataAsync(Guid documentFileId, string? subject, string? grade, string? year);
    Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken);
}
```

| 方法 | 职责 | 调用时机 |
|------|------|---------|
| `EnsureIndexAsync` | 确保索引存在（不存在则创建） | 服务启动时（`Program.cs`） |
| `IndexParseBlocksAsync` | 将 parse 的所有 blocks 索引到 OpenSearch | MinerU 解析完成后 |
| `DeleteParseIndexAsync` | 按 `parse_id` 删除索引文档 | 删除 parse 记录后 |
| `DeleteDocumentFileIndexAsync` | 按 `document_file_id` 删除索引文档 | 删除文档文件后 |
| `UpdateDocumentFileMetadataAsync` | 按 `document_file_id` 刷新 subject/grade/year | 元数据更新后（手动或 LLM） |
| `ExactSearchAsync` | OpenSearch BM25 搜索 | HTTP 搜索请求 |

### 1.2 ISearchDomainService（`src/Domain/Services/ISearchDomainService.cs`）

```csharp
public interface ISearchDomainService
{
    Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken);
}
```

薄封装 `ISearchIndexService.ExactSearchAsync`，异常时降级为空结果。

### 1.3 领域模型（`src/Domain/Models/`）

```csharp
// SearchResultModel.cs
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
    public DateTimeOffset? CreatedAt { get; set; }
}

// SearchFilterModel.cs
public class SearchFilterModel
{
    public string? DocumentTitle { get; set; }
    public string? Subject { get; set; }
    public string? Grade { get; set; }
    public string? Year { get; set; }
}

// SearchMatchType.cs
public static class SearchMatchType
{
    public const string ExactPhrase = "exact_phrase";
    public const string Stemmed = "stemmed";
    public const string ExactWord = "exact_word";
}

// OpenSearchOptions.cs
public class OpenSearchOptions
{
    public string Url { get; set; } = "http://localhost:9200";
    public string IndexName { get; set; } = "doclibrary-segments";
}
```

> 注：`SearchResultModel` / `SearchFilterModel` / `OpenSearchOptions` / `SearchMatchType` 分别位于各自同名文件中，**不存在** `SearchConfig.cs`。

## 2. OpenSearch 索引规格（`BuildIndexBody`）

### 2.1 Settings

```json
{
  "index": { "number_of_shards": 1, "number_of_replicas": 0 },
  "analysis": {
    "analyzer": {
      "english_custom": { "type": "custom", "tokenizer": "standard", "filter": ["lowercase", "english_stop", "english_stemmer"] },
      "english_phrase": { "type": "custom", "tokenizer": "standard", "filter": ["lowercase"] }
    },
    "filter": {
      "english_stop": { "type": "stop", "stopwords": "_english_" },
      "english_stemmer": { "type": "stemmer", "language": "english" }
    }
  }
}
```

- `english_custom`：标准分词 + 小写 + 停用词 + 词干提取（用于单词匹配，扩展召回）。
- `english_phrase`：标准分词 + 小写（用于短语匹配，保留短语完整性，不做词干提取）。

### 2.2 Mappings

| 字段 | 类型 | 说明 |
|------|------|------|
| `parse_id` | keyword | 按 parse 删除 |
| `document_file_id` | keyword | 按文档删除 |
| `file_name` | keyword | 搜索结果展示 |
| `block_id` | keyword | 唯一标识 |
| `block_type` | keyword | 版面块类型 |
| `sort_index` | integer | 块顺序 |
| `image_id` | keyword | 图片 ID |
| `subject` | keyword | 学科过滤 |
| `grade` | keyword | 年级过滤 |
| `year` | keyword | 年份过滤 |
| `page_number` | integer | 页码 |
| `text` | text (english_custom) | 索引内容；子字段 `exact`（english_phrase）、`keyword`（ignore_above=256） |
| `created_at` | date | 创建时间 |

> 已移除 legacy 字段：`document_id` / `document_title` / `sentence_id` / `question_id` / `segment_type` / `start_offset` / `end_offset`。

## 3. 索引写入规格（`IndexParseBlocksAsync`）

### 3.1 输入

| 参数 | 类型 | 说明 |
|------|------|------|
| `parseId` | Guid | 解析记录 ID |
| `documentFileId` | Guid | 文档文件 ID |
| `fileName` | string | 文件名（搜索结果展示） |
| `subject` | string? | 学科（从 `document_files` 读取） |
| `grade` | string? | 年级 |
| `year` | string? | 年份 |

### 3.2 数据流

```
1. 通过 IDocumentParseBlockRepository.GetByParseIdAsync(parseId) 读取 blocks
2. 若 blocks 为空 → LogWarning 返回
3. 遍历 blocks，跳过 string.IsNullOrWhiteSpace(TextContent) 的 block
4. 构建 bulk 请求（每 block 2 行：index 指令 + 文档），_id = $"block_{block.Id}"
5. 文档字段：parse_id, document_file_id, file_name, subject, grade, year,
            page_number (= block.PageId), block_id (= block.Id), block_type,
            text (= block.TextContent), sort_index, image_id, created_at
6. 调用 _client.BulkAsync 提交
7. 成功/失败均记录日志
```

### 3.3 幂等性

`_id = $"block_{block.Id}"`，同一 block 重复索引会覆盖已有文档，不会产生重复。

## 4. 索引删除规格

### 4.1 DeleteParseIndexAsync

- 构建 `delete_by_query`：`{ query: { term: { parse_id: parseId.ToString() } } }`
- 调用 `_client.DeleteByQueryAsync`
- 成功/失败记录日志

### 4.2 DeleteDocumentFileIndexAsync

- 构建 `delete_by_query`：`{ query: { term: { document_file_id: documentFileId.ToString() } } }`
- 调用 `_client.DeleteByQueryAsync`
- 成功/失败记录日志

> 两个删除方法**不记录** `deletedCount`，仅记录成功/失败状态码。

## 5. 元数据同步规格（`UpdateDocumentFileMetadataAsync`）

- 构建 `update_by_query`：`{ query: { term: { document_file_id } }, script: { source: "ctx._source.subject = params.subject; ...", params: { subject, grade, year } } }`
- 调用 `_client.UpdateByQueryAsync`
- 成功/失败记录日志

## 6. 搜索规格（`ExactSearchAsync` / `BuildSearchBody`）

### 6.1 查询构建

| 参数 | 处理 |
|------|------|
| `phrase = true` | `match_phrase` 查询 `text.exact` 字段（english_phrase 分析器） |
| `phrase = false` | `match` 查询 `text` 字段（english_custom 分析器，含词干提取） |
| filter.Subject | `term: { subject: { value } }` |
| filter.Grade | `term: { grade: { value } }` |
| filter.Year | `term: { year: { value } }` |
| filter.DocumentTitle | `term: { file_name: { value } }`（注意：C# 属性名为 `DocumentTitle`，映射到索引字段 `file_name`） |

- 无 filter 时 query 直接为 mainQuery；有 filter 时包装为 `bool { must: mainQuery, filter: [...] }`。
- 空字符串 / null 的 filter 字段**不参与**过滤（`BuildSearchBody` 中用 `string.IsNullOrEmpty` 判断）。

### 6.2 排序与分页

- 排序：`_score desc` → `block_id asc`（保证分页稳定）。
- 分页：`search_after` 游标。`pageToken` 为上一页最后一条 `sort` 数组的 Base64 编码；无效 Base64 解码失败时从第一页开始（LogDebug，不抛异常）。
- `nextToken` 生成条件：`results.Count == pageSize` 时取最后一条 sort 数组 Base64 编码；否则为 `null`。

### 6.3 高亮

- `highlight` 启用 `text` 字段高亮，`pre_tags = ["<em>"]`，`post_tags = ["</em>"]`。
- 解析响应时优先使用 `highlight.text[0]` 作为 `AssociatedText`，无高亮时回退到 `_source.text`。

### 6.4 响应解析（`ParseSearchResponse`）

| 响应字段 | 读取来源 |
|---------|----------|
| `DocumentName` | `_source.file_name` |
| `PageNumber` | `_source.page_number` |
| `AssociatedText` | `highlight.text[0]`（优先）或 `_source.text` |
| `Score` | `_score`（缺失默认 0） |
| `MatchType` | `phrase ? "exact_phrase" : "stemmed"` |
| `SegmentId` | `_source.block_id` |
| `StartOffset` | 0（blocks 无 offset） |
| `EndOffset` | 0（blocks 无 offset） |
| `CreatedAt` | `_source.created_at`（解析为 DateTimeOffset，失败为 null） |

- `TotalCount` 取自 `hits.total.value`。
- `_score` 缺失时 `Score = 0`；`_source` 字段缺失时使用默认值。

## 7. HTTP 端点规格

### 7.1 GET /admin/documents/search

定义于 `DocumentSearchEndpoints.cs`，映射路径 `/admin/documents/search`。

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `query` | string | (必填) | 搜索关键词 |
| `phrase` | bool | `false` | 是否短语匹配 |
| `pageSize` | int | `20` | 每页结果数（1-100，超过 100 静默截断） |
| `pageToken` | string? | `null` | 游标分页 token |
| `subject` | string? | `null` | 按学科筛选 |
| `grade` | string? | `null` | 按年级筛选 |
| `year` | string? | `null` | 按年份筛选 |
| `documentTitle` | string? | `null` | 按文档标题筛选（映射到索引 `file_name`） |

**成功响应 (200 OK)**：
```json
{
  "results": [
    {
      "documentName": "lecture.pdf",
      "pageNumber": 1,
      "associatedText": "<em>Hello</em> world",
      "score": 1.5,
      "matchType": "stemmed",
      "segmentId": "blk-001",
      "startOffset": 0,
      "endOffset": 0,
      "createdAt": "2026-07-04T10:00:00.0000000+00:00"
    }
  ],
  "totalCount": 42,
  "nextPageToken": ""
}
```

**校验失败响应 (400 Bad Request)**：
```json
{ "success": false, "message": "Query cannot be empty", "errorCode": "DOCLIBRARY_QUERY_REQUIRED" }
{ "success": false, "message": "Query exceeds 200 characters", "errorCode": "DOCLIBRARY_QUERY_TOO_LONG" }
```

> 注：成功响应**不**包含 `success` 字段；错误消息为**英文**（非中文）。`nextPageToken` 无更多结果时为空字符串 `""`。

### 7.2 搜索降级

- `SearchDomainService.ExactSearchAsync` 捕获 `ISearchIndexService.ExactSearchAsync` 抛出的任意异常，LogWarning 后返回 `(Results: [], TotalCount: 0, NextToken: null)`。
- `OpenSearchIndexService.ExactSearchAsync` 在 OpenSearch 返回非 200 时抛出 `InvalidOperationException`。

## 8. 错误处理表

| 场景 | 处理 |
|------|------|
| 查询词为空 | HTTP 400，`DOCLIBRARY_QUERY_REQUIRED` |
| 查询词超过 200 字符 | HTTP 400，`DOCLIBRARY_QUERY_TOO_LONG` |
| `pageSize` 超限 | 静默截断为 100 |
| blocks 为空（索引时） | LogWarning 返回，不抛异常 |
| OpenSearch 连接失败（索引/删除） | 抛异常，调用方捕获后 LogWarning，不阻塞主流程 |
| `text_content` 为空 | 跳过该 block |
| OpenSearch 搜索非 200 | 抛 `InvalidOperationException`，`SearchDomainService` 捕获后返回空结果 |
| `pageToken` 解码失败 | LogDebug，从第一页开始 |
| 元数据同步失败 | LogWarning，不阻塞 |

## 9. 实现步骤

### 9.1 索引写入（`MinerUFileParseWorker` 集成）

在 `PersistParseResultAsync` 和 `PersistMergedChunkResultsAsync` 中（仅 `status == DocumentParseStatus.Parsed` 时）：

```csharp
// 解析完成 → 索引 blocks（best-effort）
try
{
    var searchIndexService = scopeProvider.GetRequiredService<ISearchIndexService>();
    await searchIndexService.IndexParseBlocksAsync(parse.Id, file.Id, file.FileName, file.Subject, file.Grade, file.Year);
}
catch (Exception ex)
{
    _logger.LogWarning(ex, "Failed to index parse {ParseId} to OpenSearch", parse.Id);
}
```

### 9.2 索引删除（`DocumentParseEndpoints` / `DocumentFileEndpoints` 集成）

- `DeleteDocumentParse`：解析记录删除后调用 `DeleteParseIndexAsync`（best-effort）。
- `DeleteDocumentFile`：数据库删除后（Step 4）调用 `DeleteDocumentFileIndexAsync`（best-effort）。
- `UpdateDocumentFileMetadata`：元数据更新后调用 `UpdateDocumentFileMetadataAsync`（best-effort）。

### 9.3 元数据同步（`MinerUFileParseWorker.AnalyzeMetadataIfMissingAsync`）

LLM 分析完成后，调用 `UpdateDocumentFileMetadataAsync(documentFileId, newSubject, newGrade, newYear)` 同步 OpenSearch 索引（best-effort）。

## 10. 配置

| 配置节 | 键 | 默认值 |
|--------|-----|--------|
| `OpenSearch` | `Url` | `http://localhost:9200` |
| `OpenSearch` | `IndexName` | `doclibrary-segments` |

- 配置绑定：`builder.Services.Configure<OpenSearchOptions>(builder.Configuration.GetSection("OpenSearch"))`。
- DI 注册：`AddSingleton<ISearchIndexService, OpenSearchIndexService>()`。
- 数据库初始化使用 `DatabaseInitializer`（无 EF Core Migration）。

## 11. 测试策略

### 11.1 单元测试（UT）

纯逻辑测试（`internal static` 方法），不依赖 OpenSearch HTTP。已实现于 `OpenSearchIndexServiceTests.cs` 与 `SearchDomainServiceTests.cs`。

- `BuildSearchBody`：query 构建、filter 构建、search_after、sort、highlight。
- `ParseSearchResponse`：正常解析、phrase MatchType、highlight 优先、nextToken 生成、空结果、缺失字段默认值。
- `BuildIndexBody`：blocks 字段存在且类型正确、无 legacy 字段、保留分析器。
- `SearchDomainService.ExactSearchAsync`：委托调用、filter 透传、pageToken 透传、异常降级、空结果。

### 11.2 集成测试（规划）

依赖真实 OpenSearch HTTP 与数据库，未实现：
- MinerU 解析完成后 OpenSearch 有 blocks 数据。
- 删除 parse / 文档后 OpenSearch 清理。
- 搜索命中 blocks 数据。

### 11.3 测试工具

- 框架：`xUnit` + `Moq` + `FluentAssertions`。
- 禁止连接真实 OpenSearch 跑单元测试。
- 禁止依赖时间戳做精确相等断言。

## 12. 影响范围

| 文件 | 影响 |
|------|------|
| `ISearchIndexService.cs` | 6 个方法签名 |
| `OpenSearchIndexService.cs` | 实现全部方法；`BuildIndexBody` / `BuildSearchBody` / `ParseSearchResponse` 为 `internal static` |
| `ISearchDomainService.cs` / `SearchDomainService.cs` | 薄封装 + 降级 |
| `DocumentSearchEndpoints.cs` | `GET /admin/documents/search` |
| `MinerUFileParseWorker.cs` | 解析完成后索引（best-effort） |
| `DocumentParseEndpoints.cs` | 删除 parse 后清理索引 |
| `DocumentFileEndpoints.cs` | 删除文件后清理索引；元数据更新后同步索引 |
| `Program.cs` | 启动时 `EnsureIndexAsync`（best-effort） |
