# ExactSearch — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：精确关键词检索提供基于 OpenSearch BM25 的关键词搜索能力。支持短语匹配和单词匹配两种模式，返回文档名、页码、上下文文本、匹配分数、匹配类型和偏移位置，支持游标分页和按文档元数据筛选。

**用户故事**：作为 Student 服务，我要通过 HTTP API 调用精确检索接口，根据关键词在文档库中搜索匹配的文本片段，系统使用 OpenSearch BM25 检索，返回包含文档名、页码、上下文文本和匹配位置的结果，让我能快速定位到用户所需的文档内容。

## 功能要求清单（可独立测试）

- [x] FR-01 查询词为空时返回 HTTP 400 Bad Request，响应体 `{ success: false, message: "DOCLIBRARY_QUERY_REQUIRED: 查询词不能为空", errorCode: "DOCLIBRARY_QUERY_REQUIRED" }`。
- [x] FR-02 查询词超过 200 字符时返回 HTTP 400 Bad Request，响应体 `{ success: false, message: "DOCLIBRARY_QUERY_TOO_LONG: 查询词超过200字符", errorCode: "DOCLIBRARY_QUERY_TOO_LONG" }`。
- [x] FR-03 `page_size` 超过 100 时静默截断为 100（不报错，不返回错误码）。
- [x] FR-04 `page_size` 默认 20，最小 1，最大 100；传入 0 或负值时使用默认值 20。
- [x] FR-05 使用 OpenSearch BM25 搜索（`ISearchIndexService.ExactSearchAsync`）。
- [x] FR-06 OpenSearch 不可用（`_searchIndexService == null`，通过构造函数可选注入 nullable 参数）或查询异常时，返回空结果并记录 LogWarning（不中断请求）。
- [x] FR-07 短语匹配（`phrase = true`）时 `Score = 1.0`，`MatchType = "exact_phrase"`。
- [x] FR-08 单词匹配（`phrase = false`）时 `MatchType = "stemmed"`（`english_custom` 分析器含词干提取）。
- [x] FR-09 游标分页：使用 `search_after` 游标，`page_token` 为上一页最后一条记录的 sort 数组 Base64 编码。
- [x] FR-10 `SearchFilter` 中空字符串字段视为不筛选（`MapFilter` 将空字符串转为 `null`）。
- [x] FR-11 返回 HTTP JSON 响应，包含 `results`、`total_count`、`next_page_token`。
- [x] FR-12 HTTP 搜索端点 `GET /admin/documents/search` 支持筛选参数：`subject`、`grade`、`year`、`documentTitle`。当提供任一筛选参数时，构造 `SearchFilterModel` 并传入 `ExactSearchAsync`。
- [x] FR-13 搜索结果包含 `createdAt` 字段（`DateTimeOffset`），取自 OpenSearch 索引中文档的 `created_at` 字段。前端检索测试页面显示该时间。

## 详细的验收标准（可自动验证）

- AC-FR-01：调用 `GET /admin/documents/search` 传入 `query = ""` 时，返回 HTTP 400 Bad Request，响应体 `{ success: false, message: "DOCLIBRARY_QUERY_REQUIRED: 查询词不能为空", errorCode: "DOCLIBRARY_QUERY_REQUIRED" }`。
- AC-FR-02：调用 `GET /admin/documents/search` 传入 `query` 长度 > 200 时，返回 HTTP 400 Bad Request，响应体 `{ success: false, message: "DOCLIBRARY_QUERY_TOO_LONG: 查询词超过200字符", errorCode: "DOCLIBRARY_QUERY_TOO_LONG" }`。
- AC-FR-03：调用 `GET /admin/documents/search` 传入 `pageSize = 101` 时，静默截断为 100，不报错。
- AC-FR-04：调用 `GET /admin/documents/search` 传入 `pageSize = 0` 时，实际使用 `pageSize = 20`；传入 `pageSize = 100` 时正常使用。
- AC-FR-05：`_searchIndexService` 不为 null 且不抛异常时，调用 `ISearchIndexService.ExactSearchAsync`。
- AC-FR-06：`_searchIndexService` 为 null 或抛异常时，捕获异常并 LogWarning，返回空结果（`results=[]`、`total_count=0`）。
- AC-FR-07：`phrase = true` 时，匹配结果的 `Score == 1.0`，`MatchType == "exact_phrase"`。
- AC-FR-08：`phrase = false` 时，OpenSearch 路径匹配结果的 `MatchType == "stemmed"`。
- AC-FR-09：`page_token` 解码为上一页最后一条记录的 sort 数组，传入 OpenSearch `search_after`；下一页 token 为本页最后一条记录的 sort 数组 Base64 编码，无更多结果时为空字符串。
- AC-FR-10：`MapFilter` 将 `SearchFilter` 中空字符串字段转为 `null`，不作为筛选条件。
- AC-FR-11：HTTP JSON 响应包含正确的 `results` 列表，`total_count` 为总匹配数，`next_page_token` 在无更多结果时为空字符串。

## 非功能需求

- **性能**：OpenSearch 搜索 P95 < 200ms。
- **安全**：查询词不包含敏感信息日志；HTTP API 错误消息不暴露内部异常堆栈。
- **可靠性**：OpenSearch 不可用时返回空结果，不中断请求。
- **可观测性**：OpenSearch 不可用时通过 `ILogger.LogWarning` 记录异常信息。

## 测试策略

- **覆盖率目标**：`SearchDomainService.ExactSearchAsync` 和 `OpenSearchIndexService.ExactSearchAsync` 代码行覆盖率 ≥ 80%。
- **必须测试的错误路径**：查询词为空、查询词过长、OpenSearch 异常返回空结果。
- **测试环境要求**：使用 `xUnit + Moq` 进行单元测试；所有外部依赖（ISearchIndexService）均可替换。
- **禁止事项**：不得连接真实 OpenSearch 实例跑单元测试；不得依赖时间戳做精确相等断言。
