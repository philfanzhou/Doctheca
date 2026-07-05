# ExactSearch — 测试计划 (TESTS)

测试工具：`xUnit + Moq`。现有测试文件：`test/Ruoyu.Study.DocLibrary.Tests/SearchDomainServiceTests.cs`、`test/Ruoyu.Study.DocLibrary.Tests/DocumentSearchEndpointsTests.cs`。

## 单元测试 — Given-When-Then 格式

### UT-01 查询词为空（验证 SPEC FR-01）

- **Given**：请求 `GET /admin/documents/search?query=&phrase=false&pageSize=10`。
- **When**：调用 `DocumentSearchEndpoints.Search`。
- **Then**：返回 HTTP 400 Bad Request，响应体 `{ success: false, message: "...DOCLIBRARY_QUERY_REQUIRED...", errorCode: "DOCLIBRARY_QUERY_REQUIRED" }`。

### UT-02 查询词超过 200 字符（验证 SPEC FR-02）

- **Given**：请求 `GET /admin/documents/search?query=<201字符>&phrase=false&pageSize=10`。
- **When**：调用 `DocumentSearchEndpoints.Search`。
- **Then**：返回 HTTP 400 Bad Request，响应体 `{ success: false, message: "...DOCLIBRARY_QUERY_TOO_LONG...", errorCode: "DOCLIBRARY_QUERY_TOO_LONG" }`。

### UT-03 page_size 超过 100 静默截断（验证 SPEC FR-03）

- **Given**：请求 `GET /admin/documents/search?query=test&pageSize=101`。
- **When**：调用 `DocumentSearchEndpoints.Search`。
- **Then**：领域服务收到 `pageSize = 100`（静默截断），请求正常处理，不返回错误。

### UT-04 page_size 默认值和修正（验证 SPEC FR-04）

- **Given**：请求 `GET /admin/documents/search?query=test&pageSize=0`。
- **When**：调用 `DocumentSearchEndpoints.Search`。
- **Then**：领域服务收到 `pageSize = 20`。

### UT-05 OpenSearch 正常返回（验证 SPEC FR-05）

- **Given**：`_searchIndexService` 不为 null，`ExactSearchAsync` 返回预设结果。
- **When**：调用 `SearchDomainService.ExactSearchAsync`。
- **Then**：返回 OpenSearch 的结果。

### UT-06 OpenSearch 不可用返回空结果（验证 SPEC FR-06）

- **Given**：`_searchIndexService` 不为 null，`ExactSearchAsync` 抛出 `InvalidOperationException`。
- **When**：调用 `SearchDomainService.ExactSearchAsync`。
- **Then**：LogWarning 被调用，返回空结果（`results=[]`、`total_count=0`）。

### UT-07 SearchFilter 空字符串不筛选（验证 SPEC FR-10）

- **Given**：`SearchFilter { Subject = "", Grade = "", Year = "", DocumentTitle = "" }`。
- **When**：调用 `MapFilter`。
- **Then**：返回的 `SearchFilterModel` 所有字段均为 `null`。

## OpenSearchIndexService 单元测试

`OpenSearchIndexService` 因 `OpenSearchLowLevelClient` 在构造函数中 `new` 创建而无法整体 Mock。为提升可测试性，将纯逻辑提取为 `internal static` 方法，直接测试。

### 可测试性重构

将以下纯逻辑提取为 `internal static` 方法（行为不变，仅拆分）：

| 方法 | 职责 |
|------|------|
| `BuildSearchBody(query, phrase, filter, pageSize, pageToken)` | 构建完整 OpenSearch 搜索请求体（含 query/filter/sort/highlight/search_after） |
| `ParseSearchResponse(responseJson, phrase, pageSize)` | 解析 OpenSearch 响应 JSON，返回 `(Results, TotalCount, NextToken)` |

### UT-OS-01 短语查询构建（phrase=true）

- **Given**：`query="machine learning"`, `phrase=true`, `filter=null`, `pageSize=10`, `pageToken=null`。
- **When**：调用 `BuildSearchBody`。
- **Then**：序列化后 JSON 包含 `match_phrase` 且字段为 `text.exact`。

### UT-OS-02 词干查询构建（phrase=false）

- **Given**：`query="machine"`, `phrase=false`, `filter=null`, `pageSize=10`, `pageToken=null`。
- **When**：调用 `BuildSearchBody`。
- **Then**：序列化后 JSON 包含 `match` 且字段为 `text`。

### UT-OS-03 过滤器构建（全字段）

- **Given**：`filter = { Subject="英语", Grade="G10", Year="2024", DocumentTitle="exam.pdf" }`。
- **When**：调用 `BuildSearchBody`。
- **Then**：序列化后 JSON 包含 `bool.must + filter`，filter 含 subject/grade/year/document_title 四个 term 子句。

### UT-OS-04 过滤器为 null 时不包装 bool

- **Given**：`filter=null`。
- **When**：调用 `BuildSearchBody`。
- **Then**：query 直接为 mainQuery，无 `bool` 包装。

### UT-OS-05 过滤器部分字段为空时跳过

- **Given**：`filter = { Subject="英语", Grade="", Year=null, DocumentTitle="" }`。
- **When**：调用 `BuildSearchBody`。
- **Then**：filter 仅含 subject 一个 term 子句。

### UT-OS-06 有效 pageToken 解码为 search_after

- **Given**：`pageToken = Base64("[1.5, \"doc1\", \"block1\"]")`。
- **When**：调用 `BuildSearchBody`。
- **Then**：序列化后 JSON 含 `search_after` 字段。

### UT-OS-07 无效 pageToken 静默忽略

- **Given**：`pageToken = "!!!invalid base64!!!"`。
- **When**：调用 `BuildSearchBody`。
- **Then**：序列化后 JSON 不含 `search_after` 字段。

### UT-OS-08 空 pageToken 不加 search_after

- **Given**：`pageToken=null` 或 `pageToken=""`。
- **When**：调用 `BuildSearchBody`。
- **Then**：序列化后 JSON 不含 `search_after` 字段。

### UT-OS-09 正常响应解析

- **Given**：含 2 条 hit 的响应 JSON（均含 `block_id` 字段）。
- **When**：调用 `ParseSearchResponse(json, phrase=false, pageSize=10)`。
- **Then**：返回 2 条结果，`SegmentId` 取自 `block_id`，`MatchType=Stemmed`。

### UT-OS-10 短语查询 MatchType=ExactPhrase

- **Given**：含 1 条 hit 的响应 JSON。
- **When**：调用 `ParseSearchResponse(json, phrase=true, pageSize=10)`。
- **Then**：结果的 MatchType=ExactPhrase。

### UT-OS-11 highlight 优先于 source.text

- **Given**：hit 同时含 `_source.text` 和 `highlight.text`。
- **When**：调用 `ParseSearchResponse`。
- **Then**：`AssociatedText` 取 highlight 值。

### UT-OS-12 满页时生成 nextToken

- **Given**：1 条 hit，`pageSize=1`。
- **When**：调用 `ParseSearchResponse`。
- **Then**：`nextToken` 不为 null（Base64 编码的 sort 数组）。

### UT-OS-13 未满页时不生成 nextToken

- **Given**：1 条 hit，`pageSize=10`。
- **When**：调用 `ParseSearchResponse`。
- **Then**：`nextToken` 为 null。

### UT-OS-14 空命中返回空结果

- **Given**：`hits.total.value=0`, `hits.hits=[]`。
- **When**：调用 `ParseSearchResponse`。
- **Then**：`Results` 为空，`TotalCount=0`，`NextToken=null`。

### UT-OS-15 缺失字段使用默认值

- **Given**：hit 的 `_source` 为空对象 `{}`。
- **When**：调用 `ParseSearchResponse`。
- **Then**：结果的 DocumentName/PageNumber/AssociatedText/SegmentId 均为默认值，Score=0。

### UT-OS-16 缺失 _score 时默认为 0

- **Given**：hit 不含 `_score` 字段。
- **When**：调用 `ParseSearchResponse`。
- **Then**：结果的 Score=0。

## 集成测试

### IT-01 端到端精确检索流程

1. 使用 Mock OpenSearch。
2. 创建已解析的文档文件，OpenSearch 索引含对应 blocks 数据。
3. 调用 `ExactSearch` 搜索关键词，断言返回正确数量和内容。
4. 使用 `page_token` 翻页，断言分页正确。

## 边界和异常测试

- EX-01 查询词恰好 200 字符 → 正常搜索，不抛异常。
- EX-02 `page_size = 1` → 正常返回 1 条结果。
- EX-03 `page_size = 100` → 正常返回最多 100 条结果。
- EX-04 无匹配结果 → `results` 为空列表，`total_count = 0`，`next_page_token = ""`。
- EX-05 `page_token` 为非法 Base64 → `search_after` 不生效，从第一页开始返回。
- EX-06 `_searchIndexService` 为 null → 直接返回空结果。

## 现有测试映射（到 SPEC 功能要求）

| 测试方法 | 验证 SPEC 项 |
| --- | --- |
| `ExactSearch_EmptyQuery_Returns400BadRequest` | FR-01 |
| `ExactSearch_QueryTooLong_Returns400BadRequest` | FR-02 |
| `ExactSearch_PageSizeOver100_SilentlyCapped` | FR-03 |
| `ExactSearch_DefaultPageSize` | FR-04 |
| `ExactSearchAsync_OpenSearchAvailable` | FR-05 |
| `ExactSearchAsync_Unavailable_ReturnsEmpty` | FR-06 |
| `MapFilter_EmptyStringToNull` | FR-10 |
