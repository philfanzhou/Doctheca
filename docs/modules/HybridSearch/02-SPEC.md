> **已移除**：HybridSearch（语义搜索）功能已于 2026-06-12 移除。本项目当前仅支持精确搜索（ExactSearch），不再依赖 Qdrant 和 Embedding API。

# HybridSearch — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：混合检索同时利用精确匹配（BM25）和语义相似度（向量检索）两路搜索文档，合并去重后按匹配类型优先级和分数排序返回结果。支持短语匹配和单词匹配两种精确模式，以及语义相似度匹配模式，显著提高召回率和相关性。

**用户故事**：作为 Student 服务，我要通过 gRPC 调用混合检索接口，同时利用精确匹配和语义相似度检索文档，系统优先使用 OpenSearch + Qdrant 进行混合搜索，不可用时回退数据库搜索，合并两路结果并去重排序后返回，让我能获得比纯精确检索更高的召回率和更相关的搜索结果。

## 功能要求清单（可独立测试）

- [ ] FR-01 与 ExactSearch 共享相同的查询词校验：查询词为空 → `INVALID_ARGUMENT (DOCRETRIEVAL_QUERY_REQUIRED)`；查询词 > 200 字符 → `INVALID_ARGUMENT (DOCRETRIEVAL_QUERY_TOO_LONG)`；`page_size` > 100 → `INVALID_ARGUMENT (DOCRETRIEVAL_PAGE_SIZE_INVALID)`。
- [ ] FR-02 `exactTopK` 默认 50，最大 200；传入 0 或负值时使用默认值 50。
- [ ] FR-03 `semanticTopK` 默认 20，最大 100；传入 0 或负值时使用默认值 20。
- [ ] FR-04 `page_size` 默认 50，最小 1，最大 100；与 ExactSearch 一致。
- [ ] FR-05 优先使用 OpenSearch 混合搜索（`ISearchIndexService.HybridSearchAsync`）。
- [ ] FR-06 OpenSearch 不可用（`_searchIndexService == null`）或查询异常时，回退数据库搜索并记录 LogWarning。
- [ ] FR-07 回退时 `exact_word` 匹配类型的 `MatchType` 降级为 `stemmed`。
- [ ] FR-08 回退搜索结果与 ExactSearch 的数据库回退逻辑相同（segments + questions 两表 `IndexOf(query, OrdinalIgnoreCase)` 匹配，仅搜索 `status == "ready"` 的文档）。
- [ ] FR-09 OpenSearch 混合搜索内部：精确结果调用 `ExactSearchAsync`，语义结果调用 `IQdrantService.SemanticSearchAsync`。
- [ ] FR-10 OpenSearch 混合搜索合并去重：按 `DocumentName + PageNumber + SegmentId` 三元组去重，精确结果优先。
- [ ] FR-11 OpenSearch 混合搜索排序：按匹配类型优先级（`exact_phrase > exact_word > stemmed > semantic`），同优先级按分数降序。
- [ ] FR-12 Qdrant 语义搜索失败时，仅返回精确搜索结果，LogWarning 记录异常。
- [ ] FR-13 `SearchFilter` 中空字符串字段视为不筛选（与 ExactSearch 一致）。
- [ ] FR-14 返回 `SearchResponse`，包含 `results`、`total_count`、`next_page_token`。

## 详细的验收标准（可自动验证）

- AC-FR-01：调用 `HybridSearch` 传入 `query = ""` 时，抛出 `RpcException(StatusCode.InvalidArgument, "DOCRETRIEVAL_QUERY_REQUIRED")`；传入 `query` 长度 > 200 时，抛出 `DOCRETRIEVAL_QUERY_TOO_LONG`；传入 `page_size > 100` 时，抛出 `DOCRETRIEVAL_PAGE_SIZE_INVALID`。
- AC-FR-02：调用 `HybridSearch` 传入 `exact_top_k = 0` 时，实际使用 `exactTopK = 50`；传入 `exact_top_k = 300` 时，修正为 `200`。
- AC-FR-03：调用 `HybridSearch` 传入 `semantic_top_k = 0` 时，实际使用 `semanticTopK = 20`；传入 `semantic_top_k = 150` 时，修正为 `100`。
- AC-FR-04：`pageSize` 修正逻辑与 ExactSearch 一致：`pageSize = request.PageSize > 0 ? Math.Min(request.PageSize, 100) : 50`。
- AC-FR-05：`_searchIndexService` 不为 null 且不抛异常时，调用 `ISearchIndexService.HybridSearchAsync`。
- AC-FR-06：`_searchIndexService` 为 null 时，直接回退 `DatabaseSearchAsync`；`_searchIndexService.HybridSearchAsync` 抛异常时，捕获异常并 LogWarning 后回退。
- AC-FR-07：回退路径中，`DatabaseSearchAsync` 返回的结果里 `MatchType == "exact_word"` 的记录被改为 `"stemmed"`。
- AC-FR-08：回退路径的 `DatabaseSearchAsync` 逻辑与 ExactSearch 相同（segments + questions 两表匹配，status == "ready" 过滤）。
- AC-FR-09：OpenSearch 混合搜索内部调用 `ExactSearchAsync` 获取精确结果，调用 `IQdrantService.SemanticSearchAsync` 获取语义结果。
- AC-FR-10：合并去重键为 `$"{DocumentName}|{PageNumber}|{SegmentId}"`，精确结果优先保留。
- AC-FR-11：排序优先级：`exact_phrase(0) > exact_word(1) > stemmed(2) > semantic(3)`，同优先级按 `Score` 降序。
- AC-FR-12：`IQdrantService.SemanticSearchAsync` 抛异常时，LogWarning 记录，仅返回精确搜索结果。
- AC-FR-13：`MapFilter` 将 `SearchFilter` 中空字符串字段转为 `null`。
- AC-FR-14：`SearchResponse` 包含合并后的 `results`、`total_count`、`next_page_token`。

## 非功能需求

- **性能**：OpenSearch + Qdrant 混合搜索 P95 < 500ms；数据库回退搜索 P95 < 2s。
- **安全**：查询词不包含敏感信息日志；gRPC 错误消息不暴露内部异常堆栈。
- **可靠性**：OpenSearch 或 Qdrant 不可用时自动回退，不影响搜索可用性。
- **可观测性**：OpenSearch 回退和 Qdrant 失败时通过 `ILogger.LogWarning` 记录异常信息。

## 测试策略

- **覆盖率目标**：`SearchDomainService.HybridSearchAsync` 和 `OpenSearchIndexService.HybridSearchAsync` 代码行覆盖率 ≥ 80%。
- **必须测试的错误路径**：查询词为空/过长、OpenSearch 异常回退、Qdrant 异常降级、MatchType 降级。
- **测试环境要求**：使用 `xUnit + Moq` 进行单元测试；所有外部依赖（ISearchIndexService、IQdrantService、仓储）均可替换。
- **禁止事项**：不得连接真实 OpenSearch 或 Qdrant 实例跑单元测试；不得依赖时间戳做精确相等断言。
