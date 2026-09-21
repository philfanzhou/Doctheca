# document_parse_blocks — 文档解析 Block 表

> 本文件是 `document_parse_blocks` 表的唯一事实源。

## 设计目的

本表存储解析产物的**结构化 block 行**。存量数据由历史 MinerU `content_list.json` 解析写入；新数据由 StructaDoc Blocks 同步映射写入（ADR-0009，`StructaDocParseResultSync`）。支持：
- 按页查询（`WHERE parse_id=? AND page_id=?`）
- 按类型查询（`WHERE block_type='equation'`）
- 全文搜索（`text_content` GIN 索引）
- 按 page 顺序获取（`ORDER BY page_id, sort_index`）

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | block 唯一标识 |
| `parse_id` | `UUID` | NOT NULL, FK → `document_parses(id)` ON DELETE CASCADE | | 关联解析 |
| `page_id` | `INT` | NOT NULL | | 所属页（0-indexed） |
| `sort_index` | `INT` | NOT NULL | | 在 page 内的阅读顺序 |
| `block_type` | `VARCHAR(20)` | NOT NULL | | 类型（存量常见值：`text` / `image` / `equation` / `code` / `table` / `list`；新记录为 StructaDoc canonical 类型：`title` / `text` / `list` / `table` / `formula` / `image` / `code` / `header` / `footer` / `footnote` / `unknown`，实际值由解析产物决定，消费方须容忍新增值） |
| `text_content` | `TEXT` | NULL | | 文本/HTML/LaTeX（block 主体） |
| `image_id` | `UUID` | NULL, FK → `document_parse_images(id)` ON DELETE SET NULL | | 仅 `block_type='image'` 时有值 |
| `block_data` | `JSONB` | NOT NULL | | ⭐ 整块原 JSON（兜底）：存量为 MinerU content_list 项原文；新记录为 StructaDoc block 的规范化 JSON（camelCase） |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | `NOW()` | 创建时间 |
| `sub_type` | `VARCHAR(50)` | NULL | | [第 2 代] minerU 二级分类（如 `image_body`/`table_caption`/`text`/`ref_text`） |
| `text_level` | `INT` | NOT NULL | `-1` | [第 2 代] 标题级别：0=正文,1=h1,2=h2...；非标题文本为 `-1` |
| `text_format` | `VARCHAR(20)` | NOT NULL | `''` | [第 2 代] 文本格式（VLM 后端）：`latex`/`markdown`/`none` |
| `bbox_x0` | `REAL` | NULL | | [第 2 代] bbox 左上 X（归一化到 0-1000 pipeline 惯例） |
| `bbox_y0` | `REAL` | NULL | | [第 2 代] bbox 左上 Y |
| `bbox_x1` | `REAL` | NULL | | [第 2 代] bbox 右下 X |
| `bbox_y1` | `REAL` | NULL | | [第 2 代] bbox 右下 Y |
| `score` | `REAL` | NULL | | [第 2 代] 置信度：存量为 minerU VLM score；新记录为 StructaDoc `confidence`（0-1） |
| `caption` | `TEXT` | NULL | | [第 2 代] 拼接 caption 文本（存量专有；新记录为 NULL） |

## 索引

| 索引名 | 列 | 说明 |
|--------|-----|------|
| `PK_document_parse_blocks` | `id` | 主键 |
| `IX_document_parse_blocks_parse_id_page_id_sort_index` | `(parse_id, page_id, sort_index)` | 按页顺序读 |
| `IX_document_parse_blocks_block_type` | `block_type` | 按类型过滤 |
| `IX_document_parse_blocks_image_id` | `image_id` | 按图片关联查询 |

## 关键设计点

- **`block_data` 存整块原 JSON**：解析产物格式演进加新字段时，**无需改 schema**，所有新字段都在 JSONB 里
- 经常查的字段（page、type、text）单独提出来建索引——查询性能 OK
- 不常查的字段（angle、formula_latex）放在 `block_data` 里——不占结构化存储空间
- `image_id` 软关联到 `document_parse_images`（SET NULL）——图片被删时 block 仍保留，但失去图片引用
- **[第 2 代] 结构化维度字段**：`sub_type`/`text_level`/`text_format`/`bbox_x0..y1`/`score`/`caption` 为独立列，支持 OpenSearch mapping 索引与 SQL 过滤；`block_data` JSONB 仍保留整块原文（兜底 + `_meta.block_data` 回挂）
- **[第 2 代] bbox 归一化**：统一 0-1000 惯例入库。存量为启发式判断（pipeline [0,1000] / VLM [0,1]×1000）；新记录由 StructaDoc 0-1 归一化坐标 ×1000 映射
- **新管线映射约定**：`page_id` = StructaDoc `pageNumber` − 1（null → 0）；`sort_index` 页内递增；`text_level` 由 subtype `heading-N` 推导；`text_format` = `contentFormat`

## 填充流程

```
StructaDoc Parse Run succeeded（StructaDocParseResultSync）
  ↓
GET /api/v1/parse-runs/{id}/blocks（limit=1000，跟随 nextSequence 翻页）
  ↓
先删后插 INSERT INTO document_parse_blocks
  对每个 block（按 sequence 排序）:
    parse_id       = 当前解析 ID
    page_id        = block.pageNumber - 1（null → 0）
    sort_index     = 在 page 内的递增索引
    block_type     = block.type
    text_content   = block.content
    block_data     = block 的规范化 JSON
    image_id       = block.assetId → document_parse_images 映射
    sub_type       = block.subtype
    text_level     = subtype heading-N → N，否则 -1
    text_format    = block.contentFormat
    bbox_x0/y0/x1/y1 = block.boundingBox ×1000（0-1 → 0-1000）
    score          = block.confidence
    caption        = NULL
```

> 存量记录的填充流程（MinerU Worker 下载 ZIP → 解析 content_list.json → DocumentParseBlockService.ParseBlock）已随 ADR-0009 移除，历史数据只读保留。

## 特殊说明

- 与 `document_parse_images` 是 `block_type='image'` 时一对一关系，但不是 DB 外键强约束（用 `image_id` 软引用）
- 删除 `document_parse` 时本表通过 CASCADE 自动删除
