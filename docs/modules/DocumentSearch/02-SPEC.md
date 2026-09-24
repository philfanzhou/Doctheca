# DocumentSearch — 详细需求规格 (SPEC)

## 功能概述

DocumentSearch 提供两大能力：
1. **OpenSearch 块索引写入**：将解析产生的 `document_parse_blocks` 索引到 OpenSearch，并在解析删除、文件删除、元数据更新时自动维护索引。
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
| `IndexParseBlocksAsync` | 将 parse 的所有 blocks 索引到 OpenSearch | 解析完成后 |
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

### 9.1 索引写入（`StructaDocParseWorker` 集成）

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

### 9.3 元数据同步（`StructaDocParseWorker.AnalyzeMetadataIfMissingAsync`）

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

纯逻辑测试（`internal static` 方法），不依赖 OpenSearch HTTP。已实现于 `OpenSearchIndexServiceTests.cs` 与 `SearchDomainServiceTests.cs`，底层分别调用 `OpenSearchIndexManager.BuildIndexBody`、`OpenSearchQueryBuilder.BuildSearchBody`、`OpenSearchResponseParser.ParseSearchResponse`。

- `BuildSearchBody`：query 构建、filter 构建、search_after、sort、highlight。
- `ParseSearchResponse`：正常解析、phrase MatchType、highlight 优先、nextToken 生成、空结果、缺失字段默认值。
- `BuildIndexBody`：blocks 字段存在且类型正确、无 legacy 字段、保留分析器。
- `SearchDomainService.ExactSearchAsync`：委托调用、filter 透传、pageToken 透传、异常降级、空结果。

### 11.2 集成测试（规划）

依赖真实 OpenSearch HTTP 与数据库，未实现：
- 解析完成后 OpenSearch 有 blocks 数据。
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
| `OpenSearchIndexService.cs` | `ISearchIndexService` facade；实际逻辑委托给 `OpenSearch/` 下的辅助类 |
| `ISearchDomainService.cs` / `SearchDomainService.cs` | 薄封装 + 降级 |
| `DocumentSearchEndpoints.cs` | `GET /admin/documents/search` |
| `StructaDocParseWorker.cs` | 解析完成后索引（best-effort） |
| `DocumentParseEndpoints.cs` | 删除 parse 后清理索引 |
| `DocumentFileEndpoints.cs` | 删除文件后清理索引；元数据更新后同步索引 |
| `Program.cs` | 启动时 `EnsureIndexAsync`（best-effort） |

---

## 13. 第 2 代 — minerU block 结构化检索规格（演进 V1）

> 第 2 代复用 V1 同一 OpenSearch 索引（`doclibrary-segments`）与同一 `StructaDocParseWorker` 入口，**不新建索引、不新建平行 endpoint、不新建 `BlockXxx` 独立模型类**。第 2 代在 V1 索引写入时**追加** minerU 维度字段与 `blockData` 内嵌对象，在 V1 endpoint `GET /admin/documents/search` 上**追加** minerU 过滤参数与回挂字段，V1 查询路径零变更。

### 13.1 第 2 代接口变更（扩展 V1 组件）

#### 13.1.1 领域模型（`src/Domain/Models/` 扩展，不新增独立类）

```csharp
// SearchResultModel.cs — 第 2 代追加 optional 字段（老前端不传时 null，向后兼容）
public class SearchResultModel
{
    // ... V1 字段不变（含 public double Score = OpenSearch _score）...
    public string? BlockData { get; set; }                  // [第 2 代] minerU 原始 JSON 原文（详情展示）
    public float[]? Bbox { get; set; }                      // [第 2 代] minerU bbox [x0,y0,x1,y1]
    public double? MineruScore { get; set; }                // [第 2 代] minerU 置信度（VLM 后端）
    // [偏差说明] SPEC 原拟 `float? Score`，但 V1 已有 `public double Score`（OpenSearch _score），
    // C# 不允许同名字段，故第 2 代新字段命名为 `MineruScore`（与 §13.9.1 `block.MineruScore` 一致）。
    // HTTP 响应 JSON 字段名仍为 `mineruScore`，前端按此 key 读取。
    public string? SubType { get; set; }                    // [第 2 代] minerU 二级分类
    public int? TextLevel { get; set; }                     // [第 2 代] 0=正文,1=h1...;非标题 null
    public string? TextFormat { get; set; }                 // [第 2 代] latex/markdown/none（VLM）
    public string? Caption { get; set; }                    // [第 2 代] 拼接 caption 文本（关键词召回）
}

// SearchFilterModel.cs — 第 2 代追加 minerU 过滤条件（V1 原有 4 项不变）
public class SearchFilterModel
{
    // ... V1 字段（DocumentTitle / Subject / Grade / Year）不变 ...
    public string? BlockType { get; set; }                  // [第 2 代] 精确匹配 minerU type
    public string? BlockSubType { get; set; }               // [第 2 代] 精确匹配 minerU sub_type
    public int? PageNumber { get; set; }                    // [第 2 代] minerU page_id
    public int? TextLevel { get; set; }                     // [第 2 代] minerU text_level
    public string? TextFormat { get; set; }                 // [第 2 代] minerU text_format
    public Guid? ParseId { get; set; }                      // [第 2 代] 缩小到某次 parse
    public Guid? DocumentFileId { get; set; }              // [第 2 代] 缩小到某文档
    public bool? HasImage { get; set; }                     // [第 2 代] 筛选有 img_path 的 block
}
```

#### 13.1.2 ISearchIndexService 扩展（同一接口追加 minerU 字段索引能力，方法签名不变）

```csharp
public interface ISearchIndexService
{
    // V1 6 个方法签名不变；第 2 代在 IndexParseBlocksAsync 实现内追加 minerU 字段
    Task IndexParseBlocksAsync(Guid parseId, Guid documentFileId, string fileName,
        string? subject, string? grade, string? year);
    // ... 其他 5 个方法不变 ...
}
```

> 第 2 代 minerU 字段在 `IndexParseBlocksAsync` 实现内**与 V1 字段构成同一 bulk 请求**（同一 `_id = $"block_{block.Id}"` 追加字段），不新增一次网络往返、不新增接口方法。

#### 13.1.3 ISearchDomainService 扩展（同一 `ExactSearchAsync` 内扩展 minerU filter 分支）

```csharp
public interface ISearchDomainService
{
    // V1 签名不变；第 2 代在 ExactSearchAsync 实现内追加 minerU filter 分支 + 回挂字段解析
    Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken);
}
```

#### 13.1.4 HTTP 端点（`DocumentSearchEndpoints.cs` 扩展，不新增文件）

```csharp
// GET /admin/documents/search — 第 2 代追加可选入参（V1 原有 query/phrase/subject/grade/year/documentTitle/pageSize/pageToken 全部保留）
// 追加：blockType / pageNumber / parseId / documentFileId / hasImage
// 当所有 minerU filter 均为 null 时，行为完全等同 V1（零回归）
public static async Task<IResults> Search(...)
```

### 13.2 minerU v1 block 字段两层清单

> `[说明] minerU v1 block 真实 schema 在本环境无法在线校验（github.com / pypi.org / opendatalab.github.io 均被网关拦截）。下表基于 `DocumentParseBlockService.ParseBlock` 实际读取路径整理（详见 [03-DESIGN.md §第 2 代演进](./03-DESIGN.md)）。`

#### Layer 1（代码已读，权威 — 进入 OpenSearch mapping）

| minerU 字段 | 类型 | 用途 | 代码引用 |
|------------|------|------|---------|
| `type` | string（必填） | block 分类；null/空/非字符串 → 整块丢弃 | `DocumentParseBlockService.cs:95-100` |
| `page_id` | int（可选，缺省 0） | 页码，0-indexed | `DocumentParseBlockService.cs:104-107` |
| `text` | string 或嵌套结构 | 文本内容，最高优先级 | `DocumentParseBlockService.cs:151`（`ExtractTextContent`） |
| `content` | string 或嵌套结构 | 文本内容，中优先级（`text` 缺失回退） | 同上 |
| `body` | string 或嵌套结构 | 文本内容，最低优先级（覆盖 table HTML 等） | 同上 |
| `img_path` | string（可选） | 仅 `type=image` 时读取，ZIP 内相对路径 | `DocumentParseBlockService.cs:119-132` |

#### Layer 2（`block_data` 兜底透传 JSONB，代码不解析 — `[待确认]`）

| 字段 | 证据性质 |
|------|---------|
| `angle` | `[推断]` — `docs/database/tables/document_parse_blocks.md:42` 文档表述，无代码读取 |
| `formula_latex` | `[推断]` — 同上 |

> `[说明] minerU v1 block 真实 schema 在本环境无法在线校验（github.com / pypi.org / opendatalab.github.io 均被网关拦截）。首版以项目代码实际读取路径（`DocumentParseBlockService.ParseBlock`）+ MinerU 公开枚举共识为准。其他 minerU 字段（`chars`/`position`/`layout_width`/`images`/`table_html` 等）以 `block_data` jsonb 入库，管理界面通过现有 `GET /admin/document-files/{id}` 查看，延至 facet 需求明确后再追加。`

### 13.3 第 2 代索引写入规格（扩展 `IndexParseBlocksAsync`，不新增方法）

- 与 V1 `IndexParseBlocksAsync` **共享同一 bulk 请求**（追加字段，不新增网络往返）。
- 每 block 文档在 V1 字段基础上追加：
  - `x0` / `y0` / `x1` / `y1` (float) — minerU `bbox` 分量（便于范围检索）
  - `score` (float) — minerU 置信度
  - `has_image` (boolean) — `img_path` 是否非空（通过 `block.ImageId != null` 判定，不二次解析 minerU JSON）
  - `sub_type` (string) — minerU 官方字段，区分 block 二级分类（caption/body/footnote 等）
  - `text_level` (int) — minerU 官方字段，标题层级（0 = 正文, 1 = h1, 2 = h2...；非标题文本写 `-1`）
  - `text_format` (string) — minerU VLM 后端特有字段（`latex` / `markdown` / `none`）；pipeline 版无此字段时索引空字符串
  - `caption` (text, english_custom) — 派生字段：将 `image_caption` / `table_caption` / `chart_caption` / `code_caption` 等文本拼接为可检索文本
  - `_meta.block_data` (object, `enabled:false`) — minerU 原始 JSON 整块回挂，**仅存不索引**（避免写入放大，NFR-11）
- ⚠️ **bbox 坐标系差异**（minerU 官方）：pipeline 后端 `bbox` 归一化到 0-1000，VLM 后端 `bbox` 归一化到 0-1（百分比）。写入 OpenSearch 前应由 `DocumentParseBlockService.ParseBlock` 统一归一化到 0-1000（pipeline 惯例），避免前端渲染/VLM-pipeline 混合索引的场景下坐标歧义。
- minerU `type` 与 V1 `block_type` 同源、`page_id` 与 V1 `page_number` 同源、`text|content|body` 与 V1 `text` 同源 → **不新增** alias 字段。
- `_id = $"block_{block.Id}"` 幂等（与 V1 一致）。
- 空 `text_content` 的 block 在 V1 被跳过；第 2 代仍跳过（无可检索内容）。
- best-effort：失败仅 LogWarning，不阻塞解析主流程。

### 13.4 第 2 代 OpenSearch mapping 追加

在 V1 `BuildIndexBody` mappings 中追加：

```json
"x0":                 { "type": "float" },
"y0":                 { "type": "float" },
"x1":                 { "type": "float" },
"y1":                 { "type": "float" },
"score":              { "type": "float" },
"has_image":          { "type": "boolean" },
"sub_type":           { "type": "keyword" },
"text_level":         { "type": "integer" },
"text_format":        { "type": "keyword" },
"caption":            { "type": "text", "analyzer": "english_custom",
                         "fields": { "keyword": { "type": "keyword", "ignore_above": 256 } } },
"_meta":              { "type": "object", "enabled": true, "dynamic": false,
                         "properties": { "block_data": { "type": "object", "enabled": false } } }
```

> `_meta.block_data` 使用 `enabled:false` —— 内容存入 `_source` 但**不建索引**，查询时通过 `_source` 回挂，满足 NFR-11。
>
> `sub_type` 是 minerU 官方字段（来自 `content_list.json`），用于区分 caption/body/footnote 等二级分类（如 `image_caption` / `image_body` / `table_footnote` / `code` / `algorithm` / `text` / `ref_text`）。
>
> `text_level` 是 minerU 官方字段：`0` = 正文文本，`1` = 一级标题，`2` = 二级标题，以此类推；非标题文本块该字段不存在（索引时写入 `-1`）。
>
> `text_format` 是 minerU VLM 后端特有字段：`latex` / `markdown` / `none`，用于区分行间公式的格式。
>
> `caption` 是派生字段：将 `image_caption` / `table_caption` / `chart_caption` / `code_caption` 等 caption 文本拼接为一段可检索文本，使关键词搜索能命中"图注/表注"内容。

### 13.5 第 2 代 HTTP 端点规格（扩展 `GET /admin/documents/search`，不新增 endpoint）

#### GET /admin/documents/search（第 2 代扩展）

定义于 `DocumentSearchEndpoints.cs`（V1 文件扩展，不新增文件）。V1 原有参数全部保留，**追加**可选 minerU 过滤参数：

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `blockType` | string? | `null` | 精确匹配 minerU `type`（对应索引字段 `block_type`）；枚举见 §13.3 minerU 字段两层清单 |
| `blockSubType` | string? | `null` | 精确匹配 minerU `sub_type`（区分 caption/body/footnote，如 `table_caption`、`code`、`algorithm`） |
| `pageNumber` | int? | `null` | minerU `page_id` 精确匹配（对应索引字段 `page_number`） |
| `textLevel` | int? | `null` | 精确匹配 minerU `text_level`（`0` = 正文，`1` = 一级标题...；传 `-1` 表示"仅非标题文本"） |
| `textFormat` | string? | `null` | 精确匹配 minerU `text_format`（`latex` / `markdown` / `none`，VLM 后端特有） |
| `parseId` | Guid? | `null` | 缩小到某次 parse |
| `documentFileId` | Guid? | `null` | 缩小到某文档 |
| `hasImage` | bool? | `null` | 筛选有 `img_path` 的 block（对应索引字段 `has_image`） |

**校验规则**：
- V1 原有校验（query 空/过长 → 400）**保留**。
- 当所有 minerU filter 均为 null 时，行为**完全等同 V1**（零回归）。
- `pageSize` 超限静默截断为 100（V1 值）；带 minerU filter 时建议前端引导 ≤ 50。

**成功响应 (200 OK)**（V1 字段 + 第 2 代追加字段）：

```json
{
  "results": [
    {
      "documentName": "lecture.pdf",
      "pageNumber": 1,
      "associatedText": "<em>如图所示</em>，AB 是⊙O 的直径...",
      "score": 1.5,
      "matchType": "stemmed",
      "segmentId": "blk-001",
      "startOffset": 0,
      "endOffset": 0,
      "createdAt": "2026-07-04T10:00:00.0000000+00:00",
      "blockData": "{\"type\":\"text\",\"text_level\":0,\"page_idx\":0,\"text\":\"...\",\"bbox\":[100,200,300,400]}",
      "bbox": [100.0, 200.0, 300.0, 400.0],
      "mineruScore": 0.97,
      "subType": null,
      "textLevel": 0,
      "textFormat": null,
      "caption": null
    }
  ],
  "totalCount": 17,
  "nextPageToken": ""
}
```

> 注：成功响应**不**包含 `success` 字段；错误消息为**英文**；`nextPageToken` 无更多结果时为空字符串 `""`。`blockData` / `bbox` / `score` / `subType` / `textLevel` / `textFormat` / `caption` 为 optional 字段，老前端不传时不展示，不影响反序列化。
>
> ⚠️ **bbox 坐标系**：响应 `bbox` = 0-1000 归一化（pipeline 惯例）；前端渲染时需按 `page_size`（来自 `GET /admin/document-files/{id}` 的 `document_files` 记录）还原为像素坐标。VLM 后端的 0-1 百分比坐标已在索引前统一归一化到 0-1000。

### 13.6 第 2 代查询构建（扩展 `BuildSearchBody`，不新增独立方法）

| 参数 | 处理 |
|------|------|
| `keyword` 非空 | `match` 查询 `text` 字段（english_custom 分析器；minerU `text|content|body` 已落地 V1 `text`，同源不复刻） |
| `blockType` 非空 | `term: { block_type: { value } }`（minerU `type` 与 V1 `block_type` 同源） |
| `blockSubType` 非空 | `term: { sub_type: { value } }`（minerU `sub_type`，区分 caption/body/footnote） |
| `pageNumber` 非空 | `term: { page_number: { value } }`（minerU `page_id` 与 V1 `page_number` 同源） |
| `textLevel` 非空 | `term: { text_level: { value } }`（minerU `text_level`，-1 = 非标题文本） |
| `textFormat` 非空 | `term: { text_format: { value } }`（VLM 后端特有：`latex` / `markdown` / `none`） |
| `parseId` 非空 | `term: { parse_id: { value } }` |
| `documentFileId` 非空 | `term: { document_file_id: { value } }` |
| `hasImage` 非空 | `term: { has_image: { value } }` |

- 多条件组合为 `bool { must: [...], filter: [... }`（keyword 走 `must`，minerU 精确项走 `filter`；V1 无 keyword 时无 minerU filter，按 V1 路径）。
- 排序：`_score desc`（有 keyword 时）→ `block_id asc`（稳定分页）；无 keyword 时按 `sort_index asc`。
- 分页：`search_after` 游标（同 V1）。
- 高亮：启用 `text` 字段高亮（`<em>...</em>`，复用 V1 高亮字段；minerU `text|content|body` 已与 V1 `text_content` 同源，无需另起 `mineru_text`）。
- `_source` 包含 `x0/y0/x1/y1/score/has_image` + `_meta.block_data`。

### 13.7 第 2 代响应解析（扩展 `ParseSearchResponse`，不新增独立方法）

在 V1 `ParseSearchResponse` 响应解析逻辑末尾追加 minerU 回挂分支：

| 第 2 代追加响应字段 | 读取来源 |
|-------------------|----------|
| `BlockData` (string?) | `_source._meta.block_data`（原文 string，第 2 代新增） |
| `Bbox` (float[]?) | `[_source.x0, _source.y0, _source.x1, _source.y1]`（第 2 代新增） |
| `MineruScore` (double?) | `_source.score`（第 2 代新增；C# 属性名 `MineruScore` 以避免与 V1 `Score`（OpenSearch `_score`）冲突，详见 §13.1.1 偏差说明） |
| `SubType` (string?) | `_source.sub_type`（第 2 代新增，caption/body/footnote 等二级分类） |
| `TextLevel` (int?) | `_source.text_level`（第 2 代新增，标题层级，-1 = 非标题文本） |
| `TextFormat` (string?) | `_source.text_format`（第 2 代新增，`latex`/`markdown` 等，VLM 后端特有） |
| `Caption` (string?) | `_source.caption`（第 2 代新增，image_footnote/table_caption 等 caption 文本拼接，用于关键词命中） |

- `TotalCount` 取自 `hits.total.value`（V1 不变）。
- `nextToken` 生成条件同 V1（`results.Count == pageSize`）。
- 当 `_meta.block_data` 缺失时回退 `BlockData = null`（不抛异常，不阻塞响应）。

### 13.8 第 2 代错误处理表（追加）

| 场景 | 处理 |
|------|------|
| V1 所有 minerU filter 均为 null | 走 V1 路径，行为完全等同（零回归） |
| `pageSize` 超限 | 静默截断为 100（V1 值不变） |
| OpenSearch 搜索非 200 | 抛 `InvalidOperationException`，`SearchDomainService` 捕获后返回空结果（V1 降级路径） |
| `_meta.block_data` 缺失 | `BlockData = null`，不阻塞响应 |
| `pageToken` 解码失败 | LogDebug，从第一页开始（V1 不变） |

> 注：**去掉** `DOCLIBRARY_FILTER_REQUIRED` 错误码 —— 第 2 代 endpoint 与 V1 共用 `GET /admin/documents/search`；V1 的 `query` 必填校验（空 → 400 `DOCLIBRARY_QUERY_REQUIRED`）**保留**作为唯一必填守卫，不需要另立 filter 必填守卫。

### 13.9 第 2 代实现步骤（扩展 V1，不新建）

#### 13.9.1 索引写入扩展（`StructaDocParseWorker`，扩展 `IndexParseBlocksAsync`）

在 `IndexBlocksToSearchAsync` 中，V1 bulk 构建后追加 minerU 维度字段：

```csharp
// 在 V1 字段构建后追加第 2 代 minerU 维度字段
x0            = block.BboxX0,
y0            = block.BboxY0,
x1            = block.BboxX1,
y1            = block.BboxY1,
score         = block.MineruScore,
has_image     = block.ImageId != null,
_meta         = new { block_data = block.BlockData }   // 整块回挂（V1 minerU 同源字段已覆盖 block_type/page_number/text）
```

> `BboxX0/Y0/X1/Y1`、`MineruScore` 在 `DocumentParseBlockService.ParseBlock` 阶段从 minerU `block_data` 抽取并写入 block 实体，索引端不二次解析 JSON。`has_image` 通过 `block.ImageId != null` 判定，避免重复解析 minerU JSON。

#### 13.9.2 领域服务与端点扩展

- `ISearchIndexService` / `OpenSearchIndexService`：**同一 `IndexParseBlocksAsync` 实现**内追加 minerU 字段；`BuildIndexBody` 追加 minerU 维度映射；**同一 `ExactSearchAsync` 实现**内追加 minerU filter 分支 + `ParseSearchResponse` 追加 minerU 回挂。**不新增接口方法、不新增独立 `Build/Parse` 方法。**
- `ISearchDomainService` / `SearchDomainService`：同一 `ExactSearchAsync` 内扩展 minerU filter 透传与回挂字段解析；降级模式复用 V1。**不新增 `BlockXxx` 方法。**
- `DocumentSearchEndpoints.cs`：同一 `GET /admin/documents/search` 端点追加可选 minerU 入参（参数为 null 时行为同 V1）。**不新增 endpoint 文件。**

#### 13.9.3 前端扩展

- doclibrary 自带前端**改造**现有 `SearchPage.vue`（V1 页保留），新增"高级筛选"抽屉 + 结果行展开 `blockData`/`bbox`/`score` 详情。**不新增**平行 `BlockSearchPage.vue`。
- 不动 Ruoyu.Admin。

### 13.10 第 2 代配置

复用 V1 `OpenSearch:Url` / `OpenSearch:IndexName`，无新增配置节。

### 13.11 第 2 代测试策略

#### 13.11.1 单元测试（UT）—— 追加到现有测试文件

不新增独立测试方法类，在现有 `OpenSearchIndexServiceTests.cs` / `SearchDomainServiceTests.cs` 追加 minerU 断言：

- `BuildSearchBody` 追加断言：minerU 过滤条件（`blockType` / `pageNumber` / `hasImage`）走 `filter` 子句；无 minerU filter 时 `BuildSearchBody` 输出与 V1 完全一致。
- `ParseSearchResponse` 追加断言：`_source._meta.block_data` 正确映射到 `BlockData`；`x0/y0/x1/y1` 组合为 `Bbox`；缺失时回退 null。
- `SearchDomainService.ExactSearchAsync` 追加断言：minerU filter 透传给索引服务；异常降级仍返回空结果。

#### 13.11.2 集成测试（规划，同 V1 集成测试体系）

- 解析完成后 OpenSearch 有 `x0/y0/x1/y1`/`score`/`has_image`/`_meta.block_data`。
- `GET /admin/documents/search?keyword=X&blockType=Y` 命中正确 block，响应回挂 `blockData`。
- `blockData` 回挂与库内存储一致（round-trip）。

### 13.12 第 2 代影响范围（演进后）

| 文件 | 影响 |
|------|------|
| `ISearchIndexService.cs` | V1 6 个方法签名不变；实现追加 minerU 字段索引（第 2 代） |
| `OpenSearchIndexService.cs` | V1 facade 追加 minerU 字段；`OpenSearchIndexManager.BuildIndexBody` 追加 minerU 维度映射；`OpenSearchQueryBuilder.BuildSearchBody` / `OpenSearchResponseParser.ParseSearchResponse` 内追加 minerU filter + 回挂分支 |
| `ISearchDomainService.cs` / `SearchDomainService.cs` | `ExactSearchAsync` 内追加 minerU filter 透传与回挂字段解析（V1 方法内扩展） |
| `DocumentSearchEndpoints.cs`（扩展，同一文件） | `GET /admin/documents/search` 追加可选 minerU 入参（V1 原有入参与校验保留） |
| `StructaDocParseWorker.cs` | `IndexBlocksToSearchAsync` 追加 minerU 维度字段（同一 bulk） |
| `SearchPage.vue`（doclibrary 自带前端 扩展，同一页面） | 加"高级筛选"抽屉 + 结果行展开详情 |
| `Program.cs` | 无变更（复用 V1 DI 注册） |
