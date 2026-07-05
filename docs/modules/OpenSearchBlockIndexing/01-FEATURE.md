# 01-FEATURE — OpenSearch Block Indexing

## 功能概述

将 MinerU 解析产生的版面块(`document_parse_blocks`)索引到 OpenSearch,实现文档解析完成后自动索引、删除解析结果/文档时自动清理索引,支撑 `GET /admin/documents/search` 精确关键词搜索。

## 背景

DocLibrary 文档解析采用 MinerU 链路,数据表为 `document_files` / `document_parses` / `document_parse_blocks` / `document_parse_images`。MinerU 链路承担文件管理、解析结果查看、QuestionBank 拉模式导入,本功能将解析后的 blocks 索引到 OpenSearch,使搜索基于 MinerU 解析的版面块(blocks)。

## 用户故事

- **作为老师**:我上传讲义并完成 MinerU 解析后,搜索功能能立即检索到讲义内容
- **作为运维**:我删除某次解析结果时,对应的 OpenSearch 索引自动清理,不残留脏数据
- **作为运维**:我删除整个文档时,该文档所有解析版本的 OpenSearch 索引自动清理
- **作为开发者**:QuestionBank 拉模式与搜索功能基于同一数据源(blocks),保证数据一致性

## 功能需求

### FR-01:解析完成后索引 blocks
- MinerU 解析状态变为 `parsed` 后,自动将该 parse 的所有 `document_parse_blocks` 索引到 OpenSearch
- 索引以 `block_{blockId}` 为 `_id`,支持幂等覆盖(重复索引不会产生重复文档)

### FR-02:单文档多次解析多次索引
- 同一 `document_file_id` 可以有多个 `parse_id`(不同模型版本或重新解析)
- 每次 parse 完成都独立索引,每个 parse 的 blocks 独立存储(`_id` 含 `blockId`,不冲突)
- 搜索时同一文档可能命中多个 parse 的 blocks(用户可接受)

### FR-03:删除 parse 结果时清理索引
- `DELETE /admin/document-parses/{parseId}` 触发时,按 `parse_id` 删除该 parse 的所有 OpenSearch 索引文档
- 清理失败不阻塞删除操作(仅记 Warning 日志)

### FR-04:删除文档时清理索引
- `DELETE /admin/document-files/{id}` 触发时,按 `document_file_id` 删除该文档所有 parse 的 OpenSearch 索引文档
- 清理失败不阻塞删除操作(仅记 Warning 日志)

### FR-05:索引字段映射
- 每个 block 作为一个 OpenSearch 文档,字段映射:
  - `parse_id`(keyword)— 支持按 parse 删除
  - `document_file_id`(keyword)— 支持按文档删除
  - `file_name`(keyword)— 文档名(搜索结果展示)
  - `subject` / `grade` / `year`(keyword)— 元数据过滤(当前留空,后续补齐)
  - `page_number`(integer)— 页码
  - `block_id`(keyword)— 唯一标识
  - `block_type`(keyword)— 版面块类型(text/image/table/equation/list/code)
  - `text`(text,english_custom 分析器)— 索引内容(来自 `text_content`)
  - `sort_index`(integer)— 块顺序
  - `image_id`(keyword)— 图片 ID(image 类型才有)
  - `created_at`(date)— 创建时间

### FR-06:搜索接口响应字段
- `GET /admin/documents/search` 响应字段映射:
  - `documentName` ← `file_name`
  - `pageNumber` ← `page_number`
  - `associatedText` ← `text`(高亮)
  - `segmentId` ← `block_id`
  - `matchType` ← phrase ? `exact_phrase` : `stemmed`
  - `startOffset` / `endOffset` ← 0(blocks 无 offset 概念)
  - `score` ← OpenSearch `_score`
  - `createdAt` ← `created_at`
- 前端 `SearchPage.vue` 无需改动

### FR-07:OpenSearch 不可用降级
- OpenSearch 不可用时,搜索返回空结果并记录 LogWarning
- 索引失败不阻塞解析流程(仅记 Warning 日志)

## 验收条件

| AC | 描述 |
|----|------|
| AC-01 | MinerU 解析完成后,该 parse 的 blocks 出现在 OpenSearch 索引中 |
| AC-02 | 同一文档多次解析,每次 parse 的 blocks 独立索引,`_id` 不冲突 |
| AC-03 | `DELETE /admin/document-parses/{parseId}` 后,该 parse 的 OpenSearch 索引文档被删除 |
| AC-04 | `DELETE /admin/document-files/{id}` 后,该文档所有 parse 的 OpenSearch 索引文档被删除 |
| AC-05 | `GET /admin/documents/search` 能搜到 MinerU 解析的 blocks |
| AC-06 | 搜索响应字段与前端 `SearchPage.vue` 兼容 |
| AC-07 | OpenSearch 不可用时,搜索返回空结果并记录 LogWarning |
| AC-08 | 索引失败不阻塞解析流程(仅记 Warning 日志) |
| AC-09 | 删除索引失败不阻塞删除操作(仅记 Warning 日志) |

## 非功能需求

| NFR | 描述 |
|-----|------|
| NFR-01 | 索引操作异步执行,不阻塞解析状态更新 |
| NFR-02 | 索引失败仅记日志,不影响解析状态 |
| NFR-03 | 删除索引使用 `delete_by_query`,支持按 `parse_id` / `document_file_id` 批量删除 |
| NFR-04 | OpenSearch 不可用时,所有索引/删除操作 best-effort(仅记日志) |
| NFR-05 | 所有操作记录结构化日志,含 `parseId` / `documentFileId` / `blockCount` |

## 数据来源

- **索引源**:`document_parse_blocks` 表(代表 MinerU 解析的结构化输出)
- **block_data 字段**:保留原始 JSON(包括 content_list v1/v2 格式),索引时不解析,仅用 `text_content` 字段
- **元数据**:`document_files` 表当前无 `subject` / `grade` / `year` 字段,索引时这些字段留空(后续补齐)

## 接口清单

本模块不新增 HTTP 端点,仅修改内部服务:

| 组件 | 修改 |
|------|------|
| `ISearchIndexService` | 新增 3 个方法:`IndexParseBlocksAsync` / `DeleteParseIndexAsync` / `DeleteDocumentFileIndexAsync` |
| `OpenSearchIndexService` | 实现新方法;`BuildIndexBody` 索引字段;`ParseSearchResponse` 读取 blocks 字段 |
| `MinerUFileParseWorker` | `PersistParseResultAsync` / `PersistMergedChunkResultsAsync` 解析完成后调用 `IndexParseBlocksAsync` |
| `DocumentParseEndpoints` | `DeleteDocumentParse` 调用 `DeleteParseIndexAsync` |
| `DocumentFileEndpoints` | `DeleteDocumentFile` 调用 `DeleteDocumentFileIndexAsync` |
