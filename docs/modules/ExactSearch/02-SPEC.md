# ExactSearch — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：精确关键词检索提供基于 BM25（OpenSearch）或数据库回退的关键词搜索能力。支持短语匹配和单词匹配两种模式，返回文档名、页码、上下文文本、匹配分数、匹配类型和偏移位置，支持游标分页和按文档元数据筛选。

**用户故事**：作为 Student 服务，我要通过 gRPC 调用精确检索接口，根据关键词在文档库中搜索匹配的文本片段，系统优先使用 OpenSearch BM25 检索，OpenSearch 不可用时回退数据库搜索，返回包含文档名、页码、上下文文本和匹配位置的结果，让我能快速定位到用户所需的文档内容。

## 功能要求清单（可独立测试）

- [x] FR-01 查询词为空时 gRPC 返回 `RpcException(StatusCode.InvalidArgument, "DOCRETRIEVAL_QUERY_REQUIRED")`。
- [x] FR-02 查询词超过 200 字符时 gRPC 返回 `RpcException(StatusCode.InvalidArgument, "DOCRETRIEVAL_QUERY_TOO_LONG")`。
- [x] FR-03 `page_size` 超过 100 时 gRPC 返回 `RpcException(StatusCode.InvalidArgument, "DOCRETRIEVAL_PAGE_SIZE_INVALID")`。
- [x] FR-04 `page_size` 默认 50，最小 1，最大 100；传入 0 或负值时使用默认值 50。
- [x] FR-05 优先使用 OpenSearch BM25 搜索（`ISearchIndexService.ExactSearchAsync`）。
- [x] FR-06 OpenSearch 不可用（`_searchIndexService == null`，通过构造函数可选注入 nullable 参数）或查询异常时，回退数据库搜索并记录 LogWarning。
- [x] FR-07 数据库回退搜索通过 `IDocumentSegmentRepository.SearchByTextAsync` 和 `IQuestionSegmentRepository.SearchByStemAsync` 执行数据库级 `LIKE` 查询匹配。
- [x] FR-08 仅搜索 `status == "ready"` 的文档。
- [x] FR-09 短语匹配（`phrase = true`）时 `Score = 1.0`，`MatchType = "exact_phrase"`。
- [x] FR-10 单词匹配（`phrase = false`）时：OpenSearch 路径 `MatchType = "stemmed"`（因为 `english_custom` 分析器含词干提取）；数据库回退路径 `MatchType = "exact_word"`（因为 `LIKE` 是精确子串匹配）。
- [x] FR-11 结果按 `DocumentName + PageNumber + SegmentId` 三元组去重，保留首次出现的记录。
- [x] FR-12 游标分页：`page_token` 为 `Base64(JSON({ "skip": N }))` 编码。数据库回退搜索通过 `SearchByTextAsync` / `SearchByStemAsync` 的 `pageSize` 和 `skip` 参数实现数据库级 `OFFSET/LIMIT` 分页。
- [x] FR-13 `SearchFilter` 中空字符串字段视为不筛选（`MapFilter` 将空字符串转为 `null`）。
- [x] FR-14 返回 `SearchResponse`，包含 `results`、`total_count`、`next_page_token`。
- [x] FR-15 HTTP 搜索测试端点 `GET /admin/documents/search-test` 支持筛选参数：`subject`、`grade`、`year`、`documentTitle`。当提供任一筛选参数时，构造 `SearchFilterModel` 并传入 `ExactSearchAsync`。
- [x] FR-16 搜索结果包含 `createdAt` 字段（`DateTimeOffset`），取自 segment/question 记录的 `CreatedAt`，用于定位记录属于哪次导入。前端检索测试页面显示该时间。

## 详细的验收标准（可自动验证）

- AC-FR-01：调用 `ExactSearch` 传入 `query = ""` 时，抛出 `RpcException(StatusCode.InvalidArgument, "DOCRETRIEVAL_QUERY_REQUIRED")`。
- AC-FR-02：调用 `ExactSearch` 传入 `query` 长度 > 200 时，抛出 `RpcException(StatusCode.InvalidArgument, "DOCRETRIEVAL_QUERY_TOO_LONG")`。
- AC-FR-03：调用 `ExactSearch` 传入 `page_size = 101` 时，抛出 `RpcException(StatusCode.InvalidArgument, "DOCRETRIEVAL_PAGE_SIZE_INVALID")`。
- AC-FR-04：调用 `ExactSearch` 传入 `page_size = 0` 时，实际使用 `pageSize = 50`；传入 `page_size = 100` 时正常使用。
- AC-FR-05：`_searchIndexService` 不为 null 且不抛异常时，调用 `ISearchIndexService.ExactSearchAsync`。
- AC-FR-06：`_searchIndexService` 为 null（构造函数传入 null）时，直接调用 `DatabaseSearchAsync`；`_searchIndexService` 抛异常时，捕获异常并 LogWarning 后回退 `DatabaseSearchAsync`。
- AC-FR-07：数据库回退搜索调用 `IDocumentSegmentRepository.SearchByTextAsync` 和 `IQuestionSegmentRepository.SearchByStemAsync` 执行数据库级 `LIKE` 查询。
- AC-FR-08：`DatabaseSearchAsync` 中 `documents.Where(d => d.Status == "ready")` 过滤非 ready 文档。
- AC-FR-09：`phrase = true` 时，匹配结果的 `Score == 1.0`，`MatchType == "exact_phrase"`。
- AC-FR-10：`phrase = false` 时，OpenSearch 路径匹配结果的 `MatchType == "stemmed"`；数据库回退路径匹配结果的 `Score == 0.8`，`MatchType == "exact_word"`。
- AC-FR-11：去重键为 `$"{DocumentName}|{PageNumber}|{SegmentId}"`，使用 `GroupBy(...).Select(g => g.First())` 保留首条。
- AC-FR-12：`page_token` 解码为 JSON 后取 `skip` 值，数据库回退搜索通过 `SearchByTextAsync` / `SearchByStemAsync` 的 `skip` 和 `pageSize` 参数实现数据库级 `OFFSET/LIMIT` 分页；下一页 token 为 `Base64(JSON({ skip = skip + pagedResults.Count }))`。
- AC-FR-13：`MapFilter` 将 `SearchFilter` 中空字符串字段转为 `null`，`GetFilteredDocumentsAsync` 传入 `null` 时不作为筛选条件。
- AC-FR-14：`SearchResponse.Results` 包含正确的 `SearchResult` 列表，`TotalCount` 为总匹配数，`NextPageToken` 在无更多结果时为空字符串。

## 非功能需求

- **性能**：OpenSearch 搜索 P95 < 200ms；数据库回退搜索 P95 < 2s（取决于文档数量）。
- **安全**：查询词不包含敏感信息日志；gRPC 错误消息不暴露内部异常堆栈。
- **可靠性**：OpenSearch 不可用时自动回退，不影响搜索可用性。
- **可观测性**：OpenSearch 回退时通过 `ILogger.LogWarning` 记录异常信息。

## 测试策略

- **覆盖率目标**：`SearchDomainService.ExactSearchAsync` 和 `DatabaseSearchAsync` 代码行覆盖率 ≥ 80%。
- **必须测试的错误路径**：查询词为空、查询词过长、page_size 超限、OpenSearch 异常回退。
- **测试环境要求**：使用 `xUnit + Moq` 进行单元测试；所有外部依赖（ISearchIndexService、仓储）均可替换。
- **禁止事项**：不得连接真实 OpenSearch 实例跑单元测试；不得依赖时间戳做精确相等断言。
