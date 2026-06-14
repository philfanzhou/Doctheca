# ExactSearch — 测试计划 (TESTS)

测试工具：`xUnit + Moq`。现有测试文件：`test/Ruoyu.Study.DocRetrieval.Tests/SearchDomainServiceTests.cs`、`test/Ruoyu.Study.DocRetrieval.Tests/DocumentRetrievalServiceImplTests.cs`。

## 单元测试 — Given-When-Then 格式

### UT-01 查询词为空（验证 SPEC FR-01）

- **Given**：`ExactSearchRequest { Query = "", Phrase = false, PageSize = 10 }`。
- **When**：调用 `ExactSearch`。
- **Then**：抛出 `RpcException`，`StatusCode == InvalidArgument`，消息包含 `DOCRETRIEVAL_QUERY_REQUIRED`。

### UT-02 查询词超过 200 字符（验证 SPEC FR-02）

- **Given**：`ExactSearchRequest { Query = "a".PadLeft(201, 'a'), Phrase = false, PageSize = 10 }`。
- **When**：调用 `ExactSearch`。
- **Then**：抛出 `RpcException`，`StatusCode == InvalidArgument`，消息包含 `DOCRETRIEVAL_QUERY_TOO_LONG`。

### UT-03 page_size 超过 100（验证 SPEC FR-03）

- **Given**：`ExactSearchRequest { Query = "test", PageSize = 101 }`。
- **When**：调用 `ExactSearch`。
- **Then**：抛出 `RpcException`，`StatusCode == InvalidArgument`，消息包含 `DOCRETRIEVAL_PAGE_SIZE_INVALID`。

### UT-04 page_size 默认值和修正（验证 SPEC FR-04）

- **Given**：`ExactSearchRequest { Query = "test", PageSize = 0 }`。
- **When**：调用 `ExactSearch`。
- **Then**：领域服务收到 `pageSize = 50`。

### UT-05 OpenSearch 正常返回（验证 SPEC FR-05）

- **Given**：`_searchIndexService` 不为 null，`ExactSearchAsync` 返回预设结果。
- **When**：调用 `SearchDomainService.ExactSearchAsync`。
- **Then**：返回 OpenSearch 的结果，不调用 `DatabaseSearchAsync`。

### UT-06 OpenSearch 异常回退（验证 SPEC FR-06）

- **Given**：`_searchIndexService` 不为 null，`ExactSearchAsync` 抛出 `InvalidOperationException`。
- **When**：调用 `SearchDomainService.ExactSearchAsync`。
- **Then**：LogWarning 被调用，回退到 `DatabaseSearchAsync` 返回结果。

### UT-07 数据库搜索 — segments 和 questions 匹配（验证 SPEC FR-07）

- **Given**：`SearchByTextAsync` 返回 1 条匹配 segment（文本包含 "hello world"），`SearchByStemAsync` 返回 1 条匹配 question（Stem 包含 "hello question"）。
- **When**：调用 `DatabaseSearchAsync("hello", false, null, 10, null)`。
- **Then**：返回 2 条结果，分别来自 segment 和 question。验证 `SearchByTextAsync` 和 `SearchByStemAsync` 被正确调用。

### UT-08 仅搜索 ready 文档（验证 SPEC FR-08）

- **Given**：两个文档，一个 status = "ready"，一个 status = "processing"，两者均包含匹配文本。
- **When**：调用 `DatabaseSearchAsync("test", false, null, 10, null)`。
- **Then**：仅返回 status = "ready" 文档的匹配结果。

### UT-09 短语匹配 Score 和 MatchType（验证 SPEC FR-09）

- **Given**：segment 文本包含完整短语 "machine learning"。
- **When**：调用 `DatabaseSearchAsync("machine learning", true, null, 10, null)`。
- **Then**：结果的 `Score == 1.0`，`MatchType == "exact_phrase"`。

### UT-10 单词匹配 Score 和 MatchType（验证 SPEC FR-10）

- **Given**：segment 文本包含单词 "machine"。
- **When**：调用 `DatabaseSearchAsync("machine", false, null, 10, null)`。
- **Then**：结果的 `Score == 0.8`，`MatchType == "exact_word"`。

### UT-11 去重逻辑（验证 SPEC FR-11）

- **Given**：同一文档同一页同一 SegmentId 在 segments 和 questions 中均匹配。
- **When**：调用 `DatabaseSearchAsync`。
- **Then**：结果去重后仅保留 1 条（首次出现的记录）。

### UT-12 游标分页（验证 SPEC FR-12）

- **Given**：`SearchByTextAsync` 和 `SearchByStemAsync` 合计返回 15 条匹配结果，`pageSize = 10`。
- **When**：第一次调用 `DatabaseSearchAsync("test", false, null, 10, null)`。
- **Then**：`SearchByTextAsync` 和 `SearchByStemAsync` 被以 `skip=0, pageSize=10` 调用，返回 10 条结果，`nextToken` 不为 null（Base64 编码的 `{"skip":10}`）。
- **When**：使用 `nextToken` 第二次调用。
- **Then**：仓储方法被以 `skip=10` 调用，返回 5 条结果，`nextToken` 为 null。

### UT-13 SearchFilter 空字符串不筛选（验证 SPEC FR-13）

- **Given**：`SearchFilter { Subject = "", Grade = "", Year = "", DocumentTitle = "" }`。
- **When**：调用 `MapFilter`。
- **Then**：返回的 `SearchFilterModel` 所有字段均为 `null`。

## 集成测试

### IT-01 端到端精确检索流程

1. 使用真实的测试数据库上下文和 Mock OpenSearch。
2. 创建 2 个 ready 文档，各包含 segments 和 questions。
3. 调用 `ExactSearch` 搜索关键词，断言返回正确数量和内容。
4. 使用 `page_token` 翻页，断言分页正确。

## 边界和异常测试

- EX-01 查询词恰好 200 字符 → 正常搜索，不抛异常。
- EX-02 `page_size = 1` → 正常返回 1 条结果。
- EX-03 `page_size = 100` → 正常返回最多 100 条结果。
- EX-04 无匹配结果 → `results` 为空列表，`total_count = 0`，`next_page_token = ""`。
- EX-05 `page_token` 为非法 Base64 → `skip` 默认为 0，从第一条开始返回。
- EX-06 `_searchIndexService` 为 null → 直接走数据库回退路径。
- EX-07 文档无 segments 和 questions → 返回空结果。

## 现有测试映射（到 SPEC 功能要求）

| 测试方法 | 验证 SPEC 项 |
| --- | --- |
| `ExactSearch_EmptyQuery_ThrowsInvalidArgument` | FR-01 |
| `ExactSearch_QueryTooLong_ThrowsInvalidArgument` | FR-02 |
| `ExactSearch_PageSizeTooLarge_ThrowsInvalidArgument` | FR-03 |
| `ExactSearch_DefaultPageSize` | FR-04 |
| `ExactSearchAsync_OpenSearchAvailable` | FR-05 |
| `ExactSearchAsync_FallbackToDatabase` | FR-06 |
| `DatabaseSearchAsync_SegmentsAndQuestionsMatch` | FR-07 |
| `DatabaseSearchAsync_OnlyReadyDocuments` | FR-08 |
| `DatabaseSearchAsync_PhraseMatchScoreAndType` | FR-09 |
| `DatabaseSearchAsync_WordMatchScoreAndType` | FR-10 |
| `DatabaseSearchAsync_DeduplicationByKey` | FR-11 |
| `DatabaseSearchAsync_CursorPagination` | FR-12 |
| `MapFilter_EmptyStringToNull` | FR-13 |
