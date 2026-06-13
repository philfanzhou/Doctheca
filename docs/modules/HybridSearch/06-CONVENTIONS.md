> **已移除**：HybridSearch（语义搜索）功能已于 2026-06-12 移除。本项目当前仅支持精确搜索（ExactSearch），不再依赖 Qdrant 和 Embedding API。

# HybridSearch — 约定与规范 (CONVENTIONS)

## 命名约定

- **命名空间**：领域代码使用 `Ruoyu.Study.DocRetrieval.Domain.*`；服务层使用 `Ruoyu.Study.DocRetrieval.Service.*`；数据库层使用 `Ruoyu.Study.DocRetrieval.Database.*`。
- **类名**：领域服务采用 `XxxDomainService`；模型采用 `XxxModel`；仓储接口采用 `IXxxRepository`/`IXxxService`；仓储实现采用 `XxxRepository`/`XxxService`。
- **方法名**：动词开头，遵循 `[动词][名词][Async]`，如 `HybridSearchAsync`、`SemanticSearchAsync`。
- **返回值类**：gRPC 层使用 proto 定义的 `SearchResponse`、`SearchResult`；领域层返回元组 `(List<SearchResultModel>, int, string?)`；语义搜索返回 `List<SearchResultModel>`。
- **私有字段**：采用 `_camelCase` 下划线前缀（如 `_searchIndexService`、`_segmentRepository`）。
- **数据库**：表名小写蛇形；列名小写蛇形（如 `document_id`、`page_number`、`sentence_id`）。

## 日志和安全要求

- **禁止记录敏感信息**：不得在日志中记录完整的查询词内容（可记录长度或哈希）；不得记录用户身份信息。
- **日志级别**：
  - `Information`：关键操作，如混合搜索成功。
  - `Warning`：非致命错误，如 OpenSearch 混合查询失败回退数据库搜索、Qdrant 语义搜索失败。必须包含异常信息以便排查。
  - `Error`：严重错误，如数据库查询失败。
- **错误消息**：对外返回的 gRPC 错误消息不应包含内部异常堆栈或数据库细节，应提供面向用户的简短描述并附带错误码。

## 错误消息格式约定

| 场景 | 错误码 | 消息文本 |
| --- | --- | --- |
| 查询词为空 | `DOCRETRIEVAL_QUERY_REQUIRED` | `"DOCRETRIEVAL_QUERY_REQUIRED: 查询词不能为空"` |
| 查询词超过 200 字符 | `DOCRETRIEVAL_QUERY_TOO_LONG` | `"DOCRETRIEVAL_QUERY_TOO_LONG: 查询词超过200字符"` |
| page_size 超过 100 | `DOCRETRIEVAL_PAGE_SIZE_INVALID` | `"DOCRETRIEVAL_PAGE_SIZE_INVALID: page_size超过最大值100"` |

> 注：HybridSearch 与 ExactSearch 共享相同的参数校验逻辑（`ValidateSearchRequest`）和错误消息格式。领域服务内部 `throw` 原始异常时应保留异常类型与消息，由 gRPC 拦截器统一转换为用户可见响应。

## MatchType 命名约定

| MatchType 值 | 含义 | 来源 |
| --- | --- | --- |
| `exact_phrase` | 精确短语匹配 | OpenSearch match_phrase / 数据库短语匹配 |
| `exact_word` | 精确单词匹配 | OpenSearch match / 数据库单词匹配 |
| `stem_match` | 词形还原匹配（降级） | 数据库回退时从 `exact_word` 降级 |
| `stemmed` | 词干匹配 | OpenSearch 词干提取结果 |
| `semantic` | 语义相似度匹配 | Qdrant 向量搜索结果 |

> 注：HybridSearch 数据库回退时，`exact_word` 降级为 `stem_match` 以区分"真正的精确匹配"和"回退后的近似匹配"。

## 测试工具要求

- **框架**：`xUnit` 2.x。
- **Mock**：`Moq` 4.x；外部依赖（ISearchIndexService、IQdrantService、仓储）必须通过 Mock 替换，不得在测试中访问真实 OpenSearch、Qdrant 或数据库。
- **断言风格**：使用 xUnit 内置 `Assert.Equal/NotEqual/True/False/NotNull/Null/Empty`；避免使用第三方 fluent 断言库以保持项目一致性。
- **异步**：测试方法必须为 `async Task`，使用 `await` 调用被测代码；禁止 `Task.Wait()` / `.Result`。
- **测试命名**：`[被测方法]_[场景]_[预期行为]`，如 `HybridSearchAsync_FallbackWithMatchTypeDowngrade_ShouldReturnStemMatch`。
- **确定性**：分页结果只断言数量和排序方向；不依赖时间戳做精确相等断言。

## 代码风格

- 遵循项目现有 C# 风格：`PascalCase` 命名空间、类名、方法名、公共字段；`_camelCase` 私有字段。
- 使用文件顶部的 `using` 指令；不要使用 `this.` 前缀访问成员。
- 构造函数注入依赖的顺序应保持与 DI 容器注册顺序一致。
- 异步方法必须返回 `Task` / `Task<T>`，以 `Async` 后缀结尾。
- 公共方法在必要时应提供参数 guard（例如 `exactTopK` 限制在 1-200 默认 50，`semanticTopK` 限制在 1-100 默认 20）。
- 禁止在领域服务中暴露 `DbContext` 或直接执行 SQL；所有数据访问必须通过仓储接口。
- `ISearchIndexService` 和 `IQdrantService` 通过 `IServiceProvider.GetService` 可选获取，不强制注册。
- 搜索结果合并时精确结果优先保留，排序按匹配类型优先级 + 分数降序。
