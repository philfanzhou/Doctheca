# DocumentSearch — OpenSearch 块索引与精确搜索

## 功能概述

将解析产生的版面块（`document_parse_blocks`）索引到 OpenSearch，并在解析完成/删除/元数据变更时自动维护索引；同时通过 HTTP API 提供基于 OpenSearch BM25 的精确关键词搜索能力，支持短语匹配、单词匹配、元数据过滤和游标分页。

本模块合并了原 ExactSearch 与 OpenSearchBlockIndexing 两个模块的全部能力。

> 本文档分两代能力，**演进**关系而非并列关系：
> - **第 1 代（已实现）**：索引写入 + 精确关键词搜索（FR-01 ~ FR-09，05-TESTS 现存用例）。
> - **第 2 代（本节新规划）**：在第 1 代之上，**演进** minerU v1 JSON 的 block 级结构化检索能力——同一 OpenSearch 索引追加 minerU 维度字段、同一 endpoint `GET /admin/documents/search` 追加 blockType/pageNumber/hasImage 过滤参数 + 命中回挂 `blockData`/`bbox`/`score`、同一前端 `SearchPage.vue` 追加"高级筛选"抽屉。**不新增**平行 endpoint、不新增 `BlockXxx` 独立模型类、不新增平行检索页。

## 背景

DocLibrary 文档解析采用 StructaDoc 管线（ADR-0009，存量数据为历史 MinerU 产物），数据表为 `document_files` / `document_parses` / `document_parse_blocks` / `document_parse_images`。搜索功能基于解析产物的版面块（blocks），每个 block 作为一个 OpenSearch 文档，索引字段来自 `document_parse_blocks` 表与 `document_files` 表的元数据。

索引与搜索共享同一套 OpenSearch mapping（`BuildIndexBody`），保证写入字段与查询字段一致。索引操作均为 best-effort：失败仅记 Warning 日志，不阻塞解析/删除主流程。搜索在 OpenSearch 不可用时降级为空结果。

## 用户故事

- **作为老师**：我上传讲义并完成解析后，搜索功能能立即检索到讲义内容。
- **作为老师**：我按学科/年级/年份筛选搜索范围，快速定位目标文档。
- **作为运维**：我删除某次解析结果时，对应的 OpenSearch 索引自动清理，不残留脏数据。
- **作为运维**：我删除整个文档时，该文档所有解析版本的 OpenSearch 索引自动清理。
- **作为运维**：我手动更新文档元数据（或 LLM 自动分析）后，OpenSearch 索引中的元数据同步刷新。

### 第 2 代用户故事（演进 V1，block 级结构化检索）

- **作为老师**：我希望在管理界面用关键词 + 块类型（如"文本块"/"图片块"）筛选，快速定位到某份讲义里所有满足条件的 block，并能直接看到该 block 在 minerU 原始 JSON 里的完整字段（bbox / page / type…），便于核对 MinerU 解析是否正确。
- **作为运维**：我希望（在同一搜索入口内）跨文档按 minerU 字段过滤（如所有 `type=equation` 的 block、所有含 image 的 block、高置信度 block），以审计解析覆盖率与质量。
- **作为技术管理者**：我确认演进方式是在现有 `GET /admin/documents/search` 上扩展，而不是另开 `GET /admin/documents/blocks` 或新建 `BlockXxx` 独立模型 / 独立前端页，避免检索逻辑分裂为两套维护。

## 功能需求

### FR-01：解析完成后索引 blocks
- 解析状态变为 `parsed` 后，自动将该 parse 的所有 `document_parse_blocks` 索引到 OpenSearch。
- 索引以 `block_{blockId}` 为 `_id`，支持幂等覆盖（重复索引不会产生重复文档）。
- 仅索引 `text_content` 非空且非纯空白的 block（`null` / 空字符串 / 纯空白无可检索内容，使用 `string.IsNullOrWhiteSpace` 判断）。

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
  - `subject` / `grade` / `year` (keyword) — 元数据过滤（索引时从 `document_files` 表读取文件已有 metadata 写入；若文件尚未填写则写入空字符串 `""`，即此时按该字段过滤不会命中）
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

### FR-10：第 2 代 — 扩展 V1 endpoint 支持 minerU 字段过滤
- 在现有 `GET /admin/documents/search` **追加**可选过滤参数（V1 原有 query/phrase/subject/grade/year/documentTitle 全部保留）：
  - `blockType`（string?，精确匹配 minerU `type`，对应索引字段 `block_type`）
  - `pageNumber`（int?，精确匹配 minerU `page_id`，对应索引字段 `page_number`）
  - `hasImage`（bool?，筛选有 `img_path` 的 block，对应索引字段 `has_image`）
  - `parseId`（Guid?，缩小到某次 parse）
  - `documentFileId`（Guid?，缩小到某文档）
- 过滤条件与 V1 keyword 条件**组合为 `AND`**（keyword 走 `must`，minerU 精确项走 `filter`）。
- 当所有 minerU filter 均为 null 时，行为**完全等同 V1**（零回归）。
- 游标分页复用 V1 `search_after` 模式（`pageToken` Base64 为最后一条 `sort` 数组）。

### FR-11：第 2 代 — 命中结果回挂 minerU block 原始 JSON
- 搜索结果每条追加 `blockData`（`document_parse_blocks.block_data` 原始 JSON 字符串），让前端能在不二次请求 `GET /admin/documents/files/{id}` 的情况下直接展示命中 block 的完整 minerU 字段。
- 同时追加 `bbox`（float[4]）与 `score`（float）字段，便于管理界面做视觉区域与置信度核对。
- V1 的 `SearchResultModel` **向后兼容扩展**：新增 `BlockData` / `Bbox` / `Score` 为 optional 字段；不传/不展示的前端（含 Ruoyu.Admin 现有引用）不受影响。**不新建** `BlockResultModel` 独立类。

### FR-12：第 2 代 — minerU 字段进入 OpenSearch mapping
- 在 V1 `BuildIndexBody` 追加 minerU 维度字段（详见 [02-SPEC.md §13.4](./02-SPEC.md)）：
  - `x0` / `y0` / `x1` / `y1`（float，bbox 分量，便于范围检索）
  - `score`（float，minerU 置信度）
  - `has_image`（bool，`img_path` 是否非空）
  - `_meta.block_data`（object, `enabled:false`，仅存不索引，用于回挂）
- minerU `type` 与 V1 `block_type` 同源、`page_id` 与 V1 `page_number` 同源、`text|content|body` 与 V1 `text` 同源 → **不新增** alias 字段，仅暴露 V1 已有字段作为过滤参数。
- `[说明] minerU v1 block 真实 schema 在本环境无法在线校验（github.com / pypi.org / opendatalab.github.io 均被网关拦截）。首版以项目代码实际读取路径（`DocumentParseBlockService.ParseBlock`）+ MinerU 公开枚举共识为准；其他 minerU 字段（`chars`/`position`/`layout_width`/`images`/`table_html` 等）以 `block_data` jsonb 入库，管理界面通过现有 `GET /admin/document-files/{id}` 查看，延至 facet 需求明确后再追加。`

### FR-13：第 2 代 — 管理界面 block 检索入口（演进 SearchPage.vue）
- 在 doclibrary **自带前端**（`src/services/ruoyu.doclibrary/frontend/`）**改造**现有 `SearchPage.vue`，新增"高级筛选"抽屉（minerU 字段过滤）+ 结果行展开显示 `blockData`/`bbox`/`score`。**不新增**平行 `BlockSearchPage.vue` 页。
- 查询面板（V1 关键词输入框保留，追加）：blockType 下拉（基于历史数据聚合候选值）、pageNumber 数字输入、parseId / documentFileId 文本输入、hasImage 复选框。
- 结果表格列（V1 列保留，追加）：块类型 / 页码 / 抽取文本（前 80 字） / 详情按钮（弹窗展示 `blockData` 格式化 JSON + bbox + score）。
- 选中行可跳转 `ParseResultsPage.vue`（已存在）定位到对应 parse。
- **不动 Ruoyu.Admin**（Ruoyu.Admin 当前无 doclibrary 相关视图，且无 BFF 转发；迁移至 Ruoyu.Admin 属于独立大工程，本模块文档不包圆）。

## 验收条件

| AC | 描述 |
|----|------|
| AC-01 | 解析完成后，该 parse 的 blocks 出现在 OpenSearch 索引中 |
| AC-02 | 同一文档多次解析，每次 parse 的 blocks 独立索引，`_id` 不冲突 |
| AC-03 | `DELETE /admin/document-parses/{parseId}` 后，该 parse 的 OpenSearch 索引文档被删除 |
| AC-04 | `DELETE /admin/document-files/{id}` 后，该文档所有 parse 的 OpenSearch 索引文档被删除 |
| AC-05 | `PUT /admin/document-files/{id}/metadata` 后，OpenSearch 索引中的 subject/grade/year 同步刷新 |
| AC-06 | `GET /admin/documents/search` 能搜到解析产生的 blocks |
| AC-07 | 短语匹配使用 `text.exact` 字段，单词匹配使用 `text` 字段 |
| AC-08 | 搜索在 OpenSearch 不可用时返回空结果并记录 LogWarning |
| AC-09 | 索引失败不阻塞解析流程（仅记 Warning 日志） |
| AC-10 | 删除索引失败不阻塞删除操作（仅记 Warning 日志） |
| AC-11 | 查询词为空/过长返回 HTTP 400 并附带对应错误码 |
| AC-12 | `page_size` 超过 100 静默截断为 100 |

### 第 2 代验收条件（演进 V1）

| AC | 描述 |
|----|------|
| AC-13 | `GET /admin/documents/search?keyword=X&blockType=Y` 按 minerU type 过滤命中正确 block |
| AC-14 | `GET /admin/documents/search` 按 `pageNumber` 精确过滤命中正确 block |
| AC-15 | `GET /admin/documents/search` 按 `hasImage=true` 筛选携带 image 的 block |
| AC-16 | `GET /admin/documents/search` 按 `parseId` / `documentFileId` 缩小范围 |
| AC-17 | V1 行为零回归：所有 minerU filter 均为 null 时，响应与改造前完全一致 |
| AC-18 | `GET /admin/documents/search` 命中结果回挂 `blockData`（与库内 `block_data` 原文 round-trip 一致） |
| AC-19 | `GET /admin/documents/search` 命中结果回挂 `bbox` / `score` 字段 |
| AC-20 | 游标分页在 minerU filter 命中集上稳定工作（连续翻页无重复/遗漏） |
| AC-21 | `SearchPage.vue` 高级筛选抽屉 + 结果行展开 `blockData` 弹窗可正常渲染 |
| AC-22 | 第 2 代 minerU schema 全面覆盖前，其他 minerU 字段（chars/position/table_html 等）以 `block_data` jsonb 入库，不阻塞首版发布 |

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

### 第 2 代非功能需求（演进 V1）

| NFR | 描述 |
|-----|------|
| NFR-08 | minerU `type` 与已有索引 `block_type` 同源、`page_id` 与 `page_number` 同源、`text|content|body` 与 `text` 同源 → 写入时不重复抽取，仅新增真正新维度（bbox/score/has_image/block_data） |
| NFR-09 | 带 minerU filter 的查询默认单页上限 50（每 block 携带完整 `blockData` JSONB，避免响应体过大）；超出截断；V1 纯 keyword 路径保持 100 |
| NFR-10 | 第 2 代检索链路 P95 < 500ms（携带 `blockData`，放宽 V1 的 200ms 目标；后续通过列投影与 gzip 压缩收敛） |
| NFR-11 | `blockData` 全文以 `_meta.block_data`（`enabled:false`）存 `_source`，不进入全文索引，避免写入放大 |
| NFR-12 | 第 2 代演进置于 doclibrary 自带前端（改造 `SearchPage.vue`），不扩大 Ruoyu.Admin 的 BFF 边界（本次范围控制） |

## 数据来源

- **索引源**：`document_parse_blocks` 表（解析结果的结构化输出），通过 `IDocumentParseBlockRepository.GetByParseIdAsync` 读取。
- **元数据来源**：`document_files` 表的 `subject` / `grade` / `year` 字段。索引时从文件记录读取写入；后续通过 `UpdateDocumentFileMetadataAsync` 同步更新。
- **第 2 代新增数据源**：`document_parse_blocks.block_data`（minerU 原始 JSONB）→ 抽取 `bbox`（→ x0/y0/x1/y1）、`score`、`has_image`、`_meta.block_data`（整块回挂）。minerU `type`/`page_id`/`text|content|body`/`img_path` 已在第 1 代库落地为 `block_type`/`page_number`/`text_content`/`ImageId`，本次不二次解析。
- **代码使用视图**（DocumentParseBlockService.ParseBlock 实际读取 minerU v1 block key）：type / page_id / text|content|body / img_path，见 [03-DESIGN.md §第 2 代演进](./03-DESIGN.md)。

## 接口清单

### 第 1 代（不变）

| 组件 | 修改 |
|------|------|
| `ISearchIndexService` | 定义 6 个方法（不变，第 2 代在同一接口追加 minerU 字段索引能力） |
| `OpenSearchIndexService` | 实现 `ISearchIndexService`；`BuildIndexBody` / `BuildSearchBody` / `ParseSearchResponse` 为 `internal static` 纯逻辑 |
| `ISearchDomainService` | 定义 `ExactSearchAsync`（薄封装 + 降级） |
| `SearchDomainService` | 实现 `ISearchDomainService`，异常时返回空结果 |
| `DocumentSearchEndpoints` | `GET /admin/documents/search` 端点 |
| `StructaDocParseWorker` | 解析完成后调用 `IndexParseBlocksAsync`（best-effort） |
| `DocumentParseEndpoints` | `DeleteDocumentParse` 调用 `DeleteParseIndexAsync`（best-effort） |
| `DocumentFileEndpoints` | `DeleteDocumentFile` 调用 `DeleteDocumentFileIndexAsync`（best-effort）；`UpdateDocumentFileMetadata` 调用 `UpdateDocumentFileMetadataAsync`（best-effort） |

### 第 2 代演进（扩展 V1 组件，不新增平行类）

| 组件 | 职责 | 调用时机 |
|------|------|---------|
| `ISearchIndexService` / `OpenSearchIndexService` | 追加 minerU 维度索引能力（在 `IndexParseBlocksAsync` 同一 bulk 追加字段：x0/y0/x1/y1/score/has_image/_meta.block_data）；追加 `ExactSearchAsync` 的 minerU filter 分支与 `blockData` 回挂解析（现有 6 个方法签名不变） | 随 V1 解析完成同一 best-effort 入口；HTTP 查询扩展 |
| `SearchResultModel`（`src/Domain/Models/` 扩展） | 追加 optional `BlockData` / `Bbox` / `Score` 字段（老前端不传时 null） | HTTP 响应契约 |
| `SearchFilterModel`（`src/Domain/Models/` 扩展） | 追加 `BlockType` / `PageNumber` / `ParseId` / `DocumentFileId` / `HasImage` 过滤条件 | HTTP 查询入参契约 |
| `DocumentSearchEndpoints`（`src/Service/Endpoints/` 扩展） | `GET /admin/documents/search` 端点追加可选 filter 入参（FR-10）；参数为 null 时行为等同 V1 | 管理界面 block 检索入口（同一页面） |
| `SearchDomainService`（扩展） | `ExactSearchAsync` 内追加 minerU filter 分支并解析 minerU 回挂字段；**不新增** `BlockXxx` 方法 | HTTP 处理委托 |
| `SearchPage.vue`（doclibrary 自带前端 扩展） | 现有搜索页加"高级筛选"抽屉 + 结果行展开矿工 U 详情；**不新增**平行 `BlockSearchPage.vue` | 前端管理界面 |

## 文档索引

| 文档 | 说明 |
|------|------|
| [01-FEATURE.md](./01-FEATURE.md) | 功能概述、用户故事、验收条件（本文档） |
| [02-SPEC.md](./02-SPEC.md) | 需求规格、接口变更、实现步骤、错误处理、测试策略 |
| [03-DESIGN.md](./03-DESIGN.md) | 设计决策、数据流、依赖关系 |
| [04-TASKS.md](./04-TASKS.md) | 任务拆解、命令速查 |
| [05-TESTS.md](./05-TESTS.md) | 单元测试表、FR/AC 映射 |
| [06-CONVENTIONS.md](./06-CONVENTIONS.md) | 命名、日志、错误消息、代码风格 |
| _第 2 代跨文档决策_ | minerU 字段维度分层（复用 V1 block_type/page_number/text + 新增 bbox/score/has_image/block_data）详见 [03-DESIGN.md §第 2 代演进](./03-DESIGN.md) |
