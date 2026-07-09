# DocumentSearch — 约定与规范 (CONVENTIONS)

## 命名约定

- **命名空间**：领域代码使用 `Ruoyu.Study.DocLibrary.Domain.*`；服务层使用 `Ruoyu.Study.DocLibrary.Service.*`；数据库层使用 `Ruoyu.Study.DocLibrary.Database.*`。
- **类名**：领域服务采用 `XxxDomainService`；模型采用 `XxxModel`；匹配类型常量采用 `static class`（`SearchMatchType`）；接口采用 `IXxxService` / `IXxxRepository`。
- **方法名**：动词开头，遵循 `[动词][名词][Async]`，如 `ExactSearchAsync`、`IndexParseBlocksAsync`、`BuildSearchBody`。
- **返回值**：HTTP 端点层返回 JSON 响应；领域层返回元组 `(List<SearchResultModel>, int, string?)`。
- **私有字段**：采用 `_camelCase` 下划线前缀（如 `_searchIndexService`、`_client`）。
- **数据库/索引字段**：小写蛇形（如 `document_file_id`、`block_id`、`text_content`、`page_number`）。
- **OpenSearch `_id`**：`block_{blockId}` 格式，保证幂等。
- **索引名**：配置驱动（`OpenSearchOptions.IndexName`，默认 `doclibrary-segments`）。
- **配置节**：`OpenSearch:Url` / `OpenSearch:IndexName`。

## 日志约定

| 场景 | 级别 | 消息模板 |
|------|------|---------|
| 索引成功 | Information | `Parse {ParseId} indexed {BlockCount} blocks to OpenSearch (FileId={FileId})` |
| 索引失败 | Warning | `Parse {ParseId} indexing failed, status code: {StatusCode}` |
| blocks 为空 | Warning | `Parse {ParseId} has no blocks to index` |
| 无可索引 blocks | Warning | `Parse {ParseId} has no indexable blocks (all text_content empty)` |
| 删除 parse 索引成功 | Information | `Parse {ParseId} search index deleted` |
| 删除 parse 索引失败 | Warning | `Failed to delete parse {ParseId} search index, status code: {StatusCode}` |
| 删除文档索引成功 | Information | `Document file {FileId} search index deleted` |
| 删除文档索引失败 | Warning | `Failed to delete document file {FileId} search index, status code: {StatusCode}` |
| 元数据同步成功 | Information | `Document file {FileId} search index metadata updated: Subject={Subject}, Grade={Grade}, Year={Year}` |
| 元数据同步失败 | Warning | `Failed to update document file {FileId} search index metadata, status code: {StatusCode}` |
| 搜索降级 | Warning | `OpenSearch query failed for query '{Query}', returning empty results` |
| pageToken 解码失败 | Debug | `Failed to decode page token, starting from first page` |
| 索引不存在（启动创建） | Information | `OpenSearch index created: {IndexName}` |
| 索引已存在 | Information | `OpenSearch index already exists: {IndexName}` |

- **禁止记录敏感信息**：不得在日志中记录完整查询词（仅记录截断/统计信息）；不得记录用户身份信息。
- **日志包含上下文**：索引/删除日志必须含 `parseId` 或 `documentFileId`，便于排查。

## 错误消息格式约定

| 场景 | HTTP | 错误码 | 消息文本 |
|------|------|--------|---------|
| 查询词为空 | 400 | `DOCLIBRARY_QUERY_REQUIRED` | `Query cannot be empty` |
| 查询词超过 200 字符 | 400 | `DOCLIBRARY_QUERY_TOO_LONG` | `Query exceeds 200 characters` |

> 注：错误消息为**英文**（非中文）；成功响应**不**包含 `success` 字段；错误响应为 `{ success: false, message: "...", errorCode: "..." }`。`pageSize` 超过 100 时静默截断，不返回错误。

## 搜索约定

- **短语匹配**（`phrase=true`）：使用 `match_phrase` + `text.exact` 字段（english_phrase 分析器，仅小写，保留短语完整性）。
- **单词匹配**（`phrase=false`）：使用 `match` + `text` 字段（english_custom 分析器，含词干提取，扩展召回）。
- **排序**：`_score desc` → `block_id asc`（保证分页稳定）。
- **分页**：`search_after` 游标；`pageToken` 为上一页最后一条 sort 数组 Base64 编码。
- **filter 映射**：`DocumentTitle` → 索引字段 `file_name`（注意名称不一致）。
- **filter 空值**：`null` 或空字符串字段不参与过滤（`BuildSearchBody` 中 `string.IsNullOrEmpty` 判断）。
- **highlight**：`pre_tags = ["<em>"]`，`post_tags = ["</em>"]`；解析时 highlight 优先于 `_source.text`。

## 索引写入约定

- 仅索引 `text_content` 非空的 block（空文本无可检索内容）。
- `_id = $"block_{block.Id}"`，保证幂等。
- bulk 请求：每 block 2 行 JSON（index 指令 + 文档），行间 `\n` 分隔，末尾 `\n`。
- 删除使用 `delete_by_query`（按 `parse_id` 或 `document_file_id`）。
- 元数据同步使用 `update_by_query` + script（按 `document_file_id` 匹配）。
- 所有 OpenSearch 写操作 best-effort：调用方捕获异常后 LogWarning，不阻塞主流程。
- **失败状态不索引**：Worker 仅当 `status == DocumentParseStatus.Parsed` 时调用 `IndexParseBlocksAsync`。

## Bulk / HTTP 客户端约定

- `OpenSearchLowLevelClient` 在构造函数中 `new` 创建（使用 `ConnectionConfiguration`，请求超时 30s）。
- 纯逻辑提取为 `internal static` 方法（`BuildIndexBody` / `BuildSearchBody` / `ParseSearchResponse`），便于单元测试。
- 通过 `InternalsVisibleTo` 暴露给测试程序集。

## 测试工具与风格

- **框架**：`xUnit` 2.x。
- **Mock**：`Moq` 4.x；`ISearchIndexService` 通过 Mock 替换，不得访问真实 OpenSearch。
- **断言**：`FluentAssertions`（`Should()` 风格）；异步方法使用 `async Task` + `await`。
- **测试命名**：`[被测方法]_[场景]_[预期行为]`，如 `BuildSearchBody_WithPhraseQuery_UsesMatchPhraseOnTextExact`。
- **确定性**：分页断言只验证数量和排序方向；不依赖时间戳做精确相等断言。
- **禁止**：不得连接真实 OpenSearch 跑单元测试；不得依赖 `Task.Wait()` / `.Result`。

## 代码风格

- 遵循项目 C# 风格：`PascalCase` 命名空间、类名、方法名；`_camelCase` 私有字段。
- 文件作用域命名空间，移除未用 `using`。
- 异步方法返回 `Task` / `Task<T>`，以 `Async` 后缀结尾；避免 `async void`。
- 注释/日志/异常消息用**英文**；业务域值（如学科名）可用中文。
- 构造函数注入依赖顺序与 DI 容器注册顺序一致。
- 领域服务不直接暴露 `DbContext` 或执行 SQL；数据访问通过仓储接口。
- `ISearchIndexService` 注册为 **Singleton**；`ISearchDomainService` 注册为 **Scoped**。
- 数据库初始化使用 `DatabaseInitializer`（无 EF Core Migration）。
