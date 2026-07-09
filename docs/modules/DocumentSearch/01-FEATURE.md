# DocumentSearch — OpenSearch 块索引与精确搜索

## 功能概述

将 MinerU 解析产生的版面块（`document_parse_blocks`）索引到 OpenSearch，并在解析完成/删除/元数据变更时自动维护索引；同时通过 HTTP API 提供基于 OpenSearch BM25 的精确关键词搜索能力，支持短语匹配、单词匹配、元数据过滤和游标分页。

本模块合并了原 ExactSearch 与 OpenSearchBlockIndexing 两个模块的全部能力。

## 背景

DocLibrary 文档解析采用 MinerU 链路，数据表为 `document_files` / `document_parses` / `document_parse_blocks` / `document_parse_images`。搜索功能基于 MinerU 解析的版面块（blocks），每个 block 作为一个 OpenSearch 文档，索引字段来自 `document_parse_blocks` 表与 `document_files` 表的元数据。

索引与搜索共享同一套 OpenSearch mapping（`BuildIndexBody`），保证写入字段与查询字段一致。索引操作均为 best-effort：失败仅记 Warning 日志，不阻塞解析/删除主流程。搜索在 OpenSearch 不可用时降级为空结果。

## 用户故事

- **作为老师**：我上传讲义并完成 MinerU 解析后，搜索功能能立即检索到讲义内容。
- **作为老师**：我按学科/年级/年份筛选搜索范围，快速定位目标文档。
- **作为运维**：我删除某次解析结果时，对应的 OpenSearch 索引自动清理，不残留脏数据。
- **作为运维**：我删除整个文档时，该文档所有解析版本的 OpenSearch 索引自动清理。
- **作为运维**：我手动更新文档元数据（或 LLM 自动分析）后，OpenSearch 索引中的元数据同步刷新。
- **作为开发者**：QuestionBank 拉模式与搜索功能基于同一数据源（blocks），保证数据一致性。

## 功能需求

### FR-01：解析完成后索引 blocks
- MinerU 解析状态变为 `parsed` 后，自动将该 parse 的所有 `document_parse_blocks` 索引到 OpenSearch。
- 索引以 `block_{blockId}` 为 `_id`，支持幂等覆盖（重复索引不会产生重复文档）。
- 仅索引 `text_content` 非空的 block（空文本无可检索内容）。

### FR-02：单文档多次解析多次索引
- 同一 `document_file_id` 可以有多个 `parse_id`（不同模型版本或重新解析）。
- 每次 parse 完成都独立索引，每个 parse 的 blocks 独立存储（`_id` 含 `blockId`，不冲突）。

### FR-03：删除 parse 结果时清理索引
- `DELETE /admin/document-parses/{parseId}` 触发时，按 `parse_id` 删除该 parse 的所有 OpenSearch 索引文档。
- 清理失败不阻塞删除操作（仅记 Warning 日志）。

### FR-04：删除文档时清理索引
- `DELETE /admin/document-files/{id}` 触发时，按 `document_file_id` 删除该文档所有 parse 的 OpenSearch 索引文档。
- 清理失败不阻塞删除操作（仅记 Warning 日志）。

### FR-05：元数据更新后同步索引
- 手动更新元数据（`PUT /admin/document-files/{id}/metadata`）或 LLM 自动分析后，按 `document_file_id` 刷新 OpenSearch 索引中的 `subject` / `grade` / `year` 字段。
- 失败仅记 Warning 日志，不阻塞。

### FR-06：索引字段映射
- 每个 block 作为一个 OpenSearch 文档，字段映射：
  - `parse_id` (keyword) — 支持按 parse 删除
  - `document_file_id` (keyword) — 支持按文档删除
  - `file_name` (keyword) — 文档名（搜索结果展示）
  - `subject` / `grade` / `year` (keyword) — 元数据过滤（索引时从 `document_files` 表读取文件已有 metadata 写入）
  - `page_number` (integer) — 页码（取自 `block.PageId`）
  - `block_id` (keyword) — 唯一标识
  - `block_type` (keyword) — 版面块类型
  - `text` (text, english_custom 分析器) — 索引内容（来自 `text_content`）
  - `sort_index` (integer) — 块顺序
  - `image_id` (keyword) — 图片 ID
  - `created_at` (date) — 创建时间

### FR-07：精确关键词搜索
- `GET /admin/documents/search` 提供基于 OpenSearch BM25 的关键词搜索。
- 支持短语匹配（`phrase=true`，使用 `text.exact` 字段）和单词匹配（`phrase=false`，使用 `text` 字段）。
- 支持按 `subject` / `grade` / `year` / `documentTitle` 元数据过滤。
- 支持游标分页（`search_after`，`pageToken` 为上一页最后一条 sort 数组的 Base64 编码）。

### FR-08：搜索降级
- OpenSearch 不可用或查询异常时，`SearchDomainService` 返回空结果并记录 LogWarning，不中断请求。

### FR-09：搜索参数校验
- 查询词为空返回 HTTP 400（`DOCLIBRARY_QUERY_REQUIRED`）。
- 查询词超过 200 字符返回 HTTP 400（`DOCLIBRARY_QUERY_TOO_LONG`）。
- `page_size` 默认 20，最小 1，最大 100（超过 100 静默截断）。

## 验收条件

| AC | 描述 |
|----|------|
| AC-01 | MinerU 解析完成后，该 parse 的 blocks 出现在 OpenSearch 索引中 |
| AC-02 | 同一文档多次解析，每次 parse 的 blocks 独立索引，`_id` 不冲突 |
| AC-03 | `DELETE /admin/document-parses/{parseId}` 后，该 parse 的 OpenSearch 索引文档被删除 |
| AC-04 | `DELETE /admin/document-files/{id}` 后，该文档所有 parse 的 OpenSearch 索引文档被删除 |
| AC-05 | `PUT /admin/document-files/{id}/metadata` 后，OpenSearch 索引中的 subject/grade/year 同步刷新 |
| AC-06 | `GET /admin/documents/search` 能搜到 MinerU 解析的 blocks |
| AC-07 | 短语匹配使用 `text.exact` 字段，单词匹配使用 `text` 字段 |
| AC-08 | 搜索在 OpenSearch 不可用时返回空结果并记录 LogWarning |
| AC-09 | 索引失败不阻塞解析流程（仅记 Warning 日志） |
| AC-10 | 删除索引失败不阻塞删除操作（仅记 Warning 日志） |
| AC-11 | 查询词为空/过长返回 HTTP 400 并附带对应错误码 |
| AC-12 | `page_size` 超过 100 静默截断为 100 |

## 非功能需求

| NFR | 描述 |
|-----|------|
| NFR-01 | 索引操作 best-effort，不阻塞解析状态更新 |
| NFR-02 | 索引/删除失败仅记日志，不影响主流程 |
| NFR-03 | 删除索引使用 `delete_by_query`，支持按 `parse_id` / `document_file_id` 批量删除 |
| NFR-04 | 搜索不可用时返回空结果，不中断请求 |
| NFR-05 | 所有操作记录结构化日志，含 `parseId` / `documentFileId` / `blockCount` |
| NFR-06 | 搜索 P95 < 200ms |
| NFR-07 | HTTP API 错误消息不暴露内部异常堆栈 |

## 数据来源

- **索引源**：`document_parse_blocks` 表（MinerU 解析的结构化输出），通过 `IDocumentParseBlockRepository.GetByParseIdAsync` 读取。
- **元数据来源**：`document_files` 表的 `subject` / `grade` / `year` 字段。索引时从文件记录读取写入；后续通过 `UpdateDocumentFileMetadataAsync` 同步更新。
- **block_data 字段**：保留原始 JSON，索引时不解析，仅用 `text_content` 字段。

## 接口清单

| 组件 | 修改 |
|------|------|
| `ISearchIndexService` | 定义 6 个方法：`EnsureIndexAsync` / `IndexParseBlocksAsync` / `DeleteParseIndexAsync` / `DeleteDocumentFileIndexAsync` / `UpdateDocumentFileMetadataAsync` / `ExactSearchAsync` |
| `OpenSearchIndexService` | 实现 `ISearchIndexService`；`BuildIndexBody` / `BuildSearchBody` / `ParseSearchResponse` 为 `internal static` 纯逻辑 |
| `ISearchDomainService` | 定义 `ExactSearchAsync`（薄封装 + 降级） |
| `SearchDomainService` | 实现 `ISearchDomainService`，异常时返回空结果 |
| `DocumentSearchEndpoints` | `GET /admin/documents/search` 端点 |
| `MinerUFileParseWorker` | 解析完成后调用 `IndexParseBlocksAsync`（best-effort） |
| `DocumentParseEndpoints` | `DeleteDocumentParse` 调用 `DeleteParseIndexAsync`（best-effort） |
| `DocumentFileEndpoints` | `DeleteDocumentFile` 调用 `DeleteDocumentFileIndexAsync`（best-effort）；`UpdateDocumentFileMetadata` 调用 `UpdateDocumentFileMetadataAsync`（best-effort） |

## 文档索引

| 文档 | 说明 |
|------|------|
| [01-FEATURE.md](./01-FEATURE.md) | 功能概述、用户故事、验收条件（本文档） |
| [02-SPEC.md](./02-SPEC.md) | 需求规格、接口变更、实现步骤、错误处理、测试策略 |
| [03-DESIGN.md](./03-DESIGN.md) | 设计决策、数据流、依赖关系 |
| [04-TASKS.md](./04-TASKS.md) | 任务拆解、命令速查 |
| [05-TESTS.md](./05-TESTS.md) | 单元测试表、FR/AC 映射 |
| [06-CONVENTIONS.md](./06-CONVENTIONS.md) | 命名、日志、错误消息、代码风格 |
