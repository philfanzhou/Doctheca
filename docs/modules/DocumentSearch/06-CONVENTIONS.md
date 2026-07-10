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

## 第 2 代日志约定（追加到 V1 日志集）

| 场景 | 级别 | 消息模板 |
|------|------|---------|
| minerU 维度字段与 `block_data` 同步落盘成功 | Debug | `Parse {ParseId} minerU fields + block_data indexed alongside V1 fields (BlockCount={BlockCount})` |
| `blockData` 单块超阈值（≥64KB） | Warning | `Parse {ParseId} block {BlockId} block_data exceeds 64KB, may impact search response size` |
| minerU filter 全空 → 走 V1 路径 | Debug | `All minerU filters null on query '{Query}', using V1 code path only` |

- **禁止记录敏感信息**：消息模板**禁止**回挂 block 原文（`blockData`），仅存 hash / 大小 / 数量等统计信息，避免日志注入与大日志量。
- **日志含上下文**：搜索日志含 `documentFileId` / `parseId` / `filterSummary`（不含原始 keyword）、`pageSize`。

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

### 第 2 代错误消息约定（无新增错误码）

> 第 2 代**不引入新错误码**。V1 既有校验继续承担：
> - query 为空 → 400 `DOCLIBRARY_QUERY_REQUIRED` （守卫条件）
> - query 超过 200 字符 → 400 `DOCLIBRARY_QUERY_TOO_LONG`
>
> minerU filter 全部可选、`null`/空字符串不参与过滤；当所有 minerU filter 均为 null 时走 V1 路径（零回归，不需要专门的全空校验错误码）。`pageSize` 超限静默截断为 100（V1 值不变）；带 minerU filter 时前端引导 ≤ 50。

## 第 2 代命名约定（扩展 V1，不新建平行类/文件）

- **类名**：不新增 `BlockXxxModel` / `BlockXxxEndpoints` / `BlockXxxPage`。第 2 代能力全部在 V1 既有类上**扩展 optional 属性**（`SearchResultModel`、`SearchFilterModel`、`DocumentSearchEndpoints`、`SearchPage.vue`）。
- **新索引字段命名**（真正的新维度，V1 没有同语义字段）：`x0`/`y0`/`x1`/`y1`（float，bbox 分量）、`score`（float）、`has_image`（bool）、`_meta.block_data`（内嵌 object, enabled:false）。
- **已复用 V1 字段命名**（同源，不双写）：`block_type` = minerU type、`page_number` = minerU page_id、`text` = minerU text。
- **OpenSearch 内嵌不索引字段**：`_meta.block_data` 启用 `enabled:false`（仅存不索引），符合 NFR-11。
- **返回 DTO 命名**：后端 → 前端 camelCase（与 V1 一致）；前端 TS 接口扩展 `SearchResultItem`（V1 接口）追加 `blockData?`/`bbox?`/`score?`。**不新增**独立 `BlockResultItem` 接口。
- **错误码**：第 2 代不引入新错误码 —— V1 的 `DOCLIBRARY_QUERY_REQUIRED`（query 空守卫）继续承担唯一必填守卫；零回归设计避免额外空 filter 校验。

## 搜索约定

- **短语匹配**（`phrase=true`）：使用 `match_phrase` + `text.exact` 字段（english_phrase 分析器，仅小写，保留短语完整性）。
- **单词匹配**（`phrase=false`）：使用 `match` + `text` 字段（english_custom 分析器，含词干提取，扩展召回）。
- **排序**：`_score desc` → `block_id asc`（保证分页稳定）。
- **分页**：`search_after` 游标；`pageToken` 为上一页最后一条 sort 数组 Base64 编码。
- **filter 映射**：`DocumentTitle` → 索引字段 `file_name`（注意名称不一致）。
- **filter 空值**：`null` 或空字符串字段不参与过滤（`BuildSearchBody` 中 `string.IsNullOrEmpty` 判断）。
- **highlight**：`pre_tags = ["<em>"]`，`post_tags = ["</em>"]`；解析时 highlight 优先于 `_source.text`。

### 第 2 代搜索约定（扩展 V1 endpoint，不新建平行路径）

- **keyword 执行**：复用 V1 `match` / `match_phrase` 查询 `text` 字段（english_custom / english_phrase 分析器）。**不新增** `mineru_text` 字段（minerU `text|content|body` 已落地 `text_content`）。
- **无 keyword 兜底排序**：按 `sort_index asc`（按块顺序）。
- **minerU 精确条件组合**：keyword 走 `must`；`blockType`（→ `block_type`）/ `pageNumber`（→ `page_number`）/ `parseId` / `documentFileId` / `hasImage`（→ `has_image`）走 `filter`。
- **零回归守卫**：当所有 minerU filter 均为 null 时，走 V1 路径（输出与改造前完全一致）。**不新增** `DOCLIBRARY_FILTER_REQUIRED` 错误码 —— V1 的 `query` 必填校验已承担唯一守卫。
- **blockData 回挂**：`_source` 含 `_meta.block_data` 原文 string，`ParseSearchResponse`（V1 方法）追加映射到 `SearchResultModel.BlockData` + 组合 `Bbox`（x0/y0/x1/y1）+ `Score`。不做二次序列化 / 反序列化。
- **分页**：`pageSize` 最大 100（V1 值）；带 minerU filter 时前端引导 ≤ 50（携带完整 `blockData` JSONB，避免响应体过大）。

## 第 2 代 minerU 字段进索引约定（演进 V1，不新建平行结构）

实现第 2 代时必须遵守的字段治理边界：

| 层 | minerU 字段 | 进入 OpenSearch mapping 形态 | 何时进入 |
|----|------------|---------------------------|---------|
| 复用 V1 已有字段（同源，零新增 mapping 成本） | `type` / `page_id` / `text|content|body` / `img_path` | 复用 V1 已有 `block_type` / `page_number` / `text`；V1 已索引且不 alias 双写 | 当前 V1 |
| 真正*新增*的第 2 代字段 | `img_path`（布尔化）→ `has_image`；`bbox` → `x0/y0/x1/y1`；`score` | `has_image`(bool) / `x0,y0,x1,y1`(float) / `score`(float)；`bbox` 拆独立 float 便于坐标范围检索 | 第 2 代首版 |
| 兜底（不进入 mapping） | `block_data` 整块原文 + 其他 minerU 字段（`chars`/`position`/`layout_width`/`images`/`angle`/`block_tags`/`content_tags`/`list_items`/`code_body`/`code_language`/`table_body`/`formula_latex`/`table_html` 等） | `_meta.block_data` 内嵌 object, `enabled:false`（仅存不索引），查询经 `_source` 回挂 | 第 2 代首版经回挂暴露 |
| 远期（facets 明确后） | minerU `chars`/`position`/`table_html`/`code_language`/`list_items` 等 | 按 facet 需求追加 mapping | 第 3 代 |

- **权威 schema 来源**：minerU 官方文档 `docs/zh/reference/output_files.md`（curl `https://raw.githubusercontent.com/opendatalab/MinerU/master/docs/zh/reference/output_files.md`）已拉取验证。
- **命名**：真正新字段**不加** `mineru_` 前缀（V1 同源字段已覆盖 type/page/text）；新增维度直接用领域命名 `x0/y0/x1/y1`/`score`/`has_image`/`sub_type`/`text_level`/`text_format`/`caption`。
- **bbox 坐标系**：pipeline 后端 0-1000，VLM 后端 0-1 百分比 — minerU 官方文档明确区分。`DocumentParseBlockService.ParseBlock` 在解析阶段统一归一化到 0-1000（pipeline 惯例）后再入库+索引；前端渲染时按文件 `page_size` 还原像素坐标。
- **`blockData` 回挂**：始终来自 `document_parse_blocks.block_data` 原文 string，不在第 2 代接口层重新序列化（保真 round-trip）。
- **`has_image` 处理**：通过 `block.ImageId != null` 判定（`DocumentParseBlockService.ParseBlock` 阶段写入 `ImageId` 导航），不重复解析 minerU JSON。
- **minerU type / sub_type 权威枚举**：详见 [03-DESIGN.md §第 2 代演进中的 minerU `content_list.json` block type 权威枚举](../DocumentSearch/03-DESIGN.md)。

> `[权威] minerU v1 block schema 已通过 docs/zh/reference/output_files.md 验证。pipeline 后端 0-1000 bbox、VLM 后端 0-1 百分比坐标系差异已在规范中明确。未列进 mapping 的 minerU 字段（chars/position/layout_width/images/angle/list_items/code_body/table_html 等）**不被假装有**，仅通过 `block_data` 兜底暴露，延至 facet 需求明确后再进入 mapping。`

## 索引写入约定

- 仅索引 `text_content` 非空的 block（空文本无可检索内容）。
- `_id = $"block_{block.Id}"`，保证幂等。
- bulk 请求：每 block 2 行 JSON（index 指令 + 文档），行间 `\n` 分隔，末尾 `\n`。
- 删除使用 `delete_by_query`（按 `parse_id` 或 `document_file_id`）。
- 元数据同步使用 `update_by_query` + script（按 `document_file_id` 匹配）。
- 所有 OpenSearch 写操作 best-effort：调用方捕获异常后 LogWarning，不阻塞主流程。
- **失败状态不索引**：Worker 仅当 `status == DocumentParseStatus.Parsed` 时调用 `IndexParseBlocksAsync`。

### 第 2 代索引写入约定（扩展 V1，不新增文件/类）

- **同一 bulk**：第 2 代 minerU 维度字段与 `block_data` 必须与 V1 字段构成**同一** bulk request，`_id = $"block_{block.Id}"` 不新增一次网络往返。
- **不丢 V1 字段**：追加字段不得覆盖 / 移除任何 V1 已有字段（`text`、`block_type`、`page_number` 等），保持 V1 查询路径零回归。
- **同源字段不双写**：minerU `type`/`page_id`/`text` 与 V1 `block_type`/`page_number`/`text` 同源，**不再** alias 为 `mineru_*`；仅真正新维度（`x0/y0/x1/y1`/`score`/`has_image`/`_meta.block_data`）新增 mapping。
- **`has_image` 来自 `ImageId` 导航**：通过 `block.ImageId != null` 判定，不访问 minerU 原文。
- **`_meta.block_data` = `block.BlockData` 原文**：整块 string 透传，不重新序列化（保真 round-trip）。
- **幂等**：`_id` 不变，重复索引覆盖，无重复文档。

## Bulk / HTTP 客户端约定

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
