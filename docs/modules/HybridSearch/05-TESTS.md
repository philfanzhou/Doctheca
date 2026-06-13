> **已移除**：HybridSearch（语义搜索）功能已于 2026-06-12 移除。本项目当前仅支持精确搜索（ExactSearch），不再依赖 Qdrant 和 Embedding API。

# HybridSearch — 测试计划 (TESTS)

测试工具：`xUnit + Moq`。现有测试文件：`test/Ruoyu.Study.DocRetrieval.Tests/SearchDomainServiceTests.cs`、`test/Ruoyu.Study.DocRetrieval.Tests/DocumentRetrievalServiceImplTests.cs`、`[当前无测试覆盖] test/Ruoyu.Study.DocRetrieval.Tests/OpenSearchIndexServiceTests.cs`。

## 单元测试 — Given-When-Then 格式

### UT-01 查询词校验（验证 SPEC FR-01）

- **Given**：`HybridSearchRequest { Query = "", Phrase = false, ExactTopK = 50, SemanticTopK = 20, PageSize = 10 }`。
- **When**：调用 `HybridSearch`。
- **Then**：抛出 `RpcException`，`StatusCode == InvalidArgument`，消息包含 `DOCRETRIEVAL_QUERY_REQUIRED`。

### UT-02 查询词超过 200 字符（验证 SPEC FR-01）

- **Given**：`HybridSearchRequest { Query = "a".PadLeft(201, 'a'), PageSize = 10 }`。
- **When**：调用 `HybridSearch`。
- **Then**：抛出 `RpcException`，`StatusCode == InvalidArgument`，消息包含 `DOCRETRIEVAL_QUERY_TOO_LONG`。

### UT-03 page_size 超过 100（验证 SPEC FR-01）

- **Given**：`HybridSearchRequest { Query = "test", PageSize = 101 }`。
- **When**：调用 `HybridSearch`。
- **Then**：抛出 `RpcException`，`StatusCode == InvalidArgument`，消息包含 `DOCRETRIEVAL_PAGE_SIZE_INVALID`。

### UT-04 exactTopK 默认值和上限（验证 SPEC FR-02）

- **Given**：`HybridSearchRequest { Query = "test", ExactTopK = 0 }`。
- **When**：调用 `HybridSearch`。
- **Then**：领域服务收到 `exactTopK = 50`。

- **Given**：`HybridSearchRequest { Query = "test", ExactTopK = 300 }`。
- **When**：调用 `HybridSearch`。
- **Then**：领域服务收到 `exactTopK = 200`。

### UT-05 semanticTopK 默认值和上限（验证 SPEC FR-03）

- **Given**：`HybridSearchRequest { Query = "test", SemanticTopK = 0 }`。
- **When**：调用 `HybridSearch`。
- **Then**：领域服务收到 `semanticTopK = 20`。

- **Given**：`HybridSearchRequest { Query = "test", SemanticTopK = 150 }`。
- **When**：调用 `HybridSearch`。
- **Then**：领域服务收到 `semanticTopK = 100`。

### UT-06 OpenSearch 正常返回（验证 SPEC FR-05）

- **Given**：`_searchIndexService` 不为 null，`HybridSearchAsync` 返回预设的合并结果。
- **When**：调用 `SearchDomainService.HybridSearchAsync`。
- **Then**：返回 OpenSearch 的合并结果，不调用 `DatabaseSearchAsync`。

### UT-07 OpenSearch 异常回退 + MatchType 降级（验证 SPEC FR-06, FR-07）

- **Given**：`_searchIndexService` 不为 null，`HybridSearchAsync` 抛出 `InvalidOperationException`；数据库搜索返回含 `MatchType = "exact_word"` 的结果。
- **When**：调用 `SearchDomainService.HybridSearchAsync`。
- **Then**：LogWarning 被调用；回退到 `DatabaseSearchAsync`；`MatchType == "exact_word"` 的记录被降级为 `"stem_match"`。

### UT-08 回退搜索与 ExactSearch 数据库回退逻辑相同（验证 SPEC FR-08）

- **Given**：`_searchIndexService` 为 null；文档 status = "ready"，segments 和 questions 包含匹配文本。
- **When**：调用 `SearchDomainService.HybridSearchAsync`。
- **Then**：回退 `DatabaseSearchAsync`，搜索逻辑与 ExactSearch 一致（segments + questions 两表 IndexOf 匹配）。

### UT-09 OpenSearch 混合搜索 — 精确+语义合并（验证 SPEC FR-09, FR-10）

- **Given**：`ExactSearchAsync` 返回 3 条精确结果，`IQdrantService.SemanticSearchAsync` 返回 2 条语义结果，其中 1 条与精确结果重复（相同 DocumentName+PageNumber+SegmentId）。
- **When**：调用 `OpenSearchIndexService.HybridSearchAsync`。
- **Then**：合并去重后共 4 条结果（3 精确 + 1 语义补充）；精确结果优先保留。

### UT-10 混合搜索排序优先级（验证 SPEC FR-11）

- **Given**：合并结果包含 `exact_phrase`、`exact_word`、`stemmed`、`semantic` 四种 MatchType。
- **When**：调用 `OpenSearchIndexService.HybridSearchAsync`。
- **Then**：结果按优先级排序：`exact_phrase` 在前，`semantic` 在后；同优先级按 `Score` 降序。

### UT-11 Qdrant 语义搜索失败降级（验证 SPEC FR-12）

- **Given**：`IQdrantService.SemanticSearchAsync` 抛出异常。
- **When**：调用 `OpenSearchIndexService.HybridSearchAsync`。
- **Then**：LogWarning 被调用；仅返回精确搜索结果。

### UT-12 SearchFilter 空字符串不筛选（验证 SPEC FR-13）

- **Given**：`SearchFilter { Subject = "", Grade = "", Year = "", DocumentTitle = "" }`。
- **When**：调用 `MapFilter`。
- **Then**：返回的 `SearchFilterModel` 所有字段均为 `null`。

## 集成测试

### IT-01 端到端混合检索流程

1. 使用真实的测试数据库上下文和 Mock OpenSearch + Qdrant。
2. 创建 2 个 ready 文档，各包含 segments 和 questions。
3. 调用 `HybridSearch` 搜索关键词，断言精确和语义结果合并正确。
4. 验证去重和排序逻辑。

## 边界和异常测试

- EX-01 查询词恰好 200 字符 → 正常搜索，不抛异常。
- EX-02 `exactTopK = 1` → 正常传递给领域服务。
- EX-03 `semanticTopK = 1` → 正常传递给领域服务。
- EX-04 无精确匹配，仅有语义匹配 → 返回语义结果。
- EX-05 无语义匹配，仅有精确匹配 → 返回精确结果。
- EX-06 无任何匹配 → `results` 为空列表，`total_count = 0`。
- EX-07 `_searchIndexService` 为 null → 直接走数据库回退 + MatchType 降级。
- EX-08 `IQdrantService` 为 null → 仅返回精确搜索结果。
- EX-09 精确结果和语义结果完全重复 → 去重后仅保留精确结果。

## 现有测试映射（到 SPEC 功能要求）

| 测试方法 | 验证 SPEC 项 |
| --- | --- |
| `HybridSearch_EmptyQuery_ThrowsInvalidArgument` | FR-01 |
| `HybridSearch_QueryTooLong_ThrowsInvalidArgument` | FR-01 |
| `HybridSearch_PageSizeTooLarge_ThrowsInvalidArgument` | FR-01 |
| `HybridSearch_ExactTopKDefaultsAndClamp` | FR-02 |
| `HybridSearch_SemanticTopKDefaultsAndClamp` | FR-03 |
| `HybridSearchAsync_OpenSearchAvailable` | FR-05 |
| `HybridSearchAsync_FallbackWithMatchTypeDowngrade` | FR-06, FR-07 |
| `HybridSearchAsync_FallbackSameAsExactSearch` | FR-08 |
| `HybridSearchAsync_MergeDeduplication` | FR-09, FR-10 |
| `HybridSearchAsync_SortPriority` | FR-11 |
| `HybridSearchAsync_QdrantFallback` | FR-12 |
| `MapFilter_EmptyStringToNull` | FR-13 |
