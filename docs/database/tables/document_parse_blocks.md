# document_parse_blocks — 文档解析 Block 表

> 本文件是 `document_parse_blocks` 表的唯一事实源。

## 设计目的

`document_parses.content_list` 存的是 MinerU 返回的**完整 JSON**（5-10 MB / 文档）。直接查询此 JSON 字段性能差。

本表把 `content_list.json` 解析为**结构化的 block 行**，支持：
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
| `block_type` | `VARCHAR(20)` | NOT NULL | | 类型（常见值：`text` / `image` / `equation` / `code` / `table` / `list`，实际值由 MinerU 输出决定） |
| `text_content` | `TEXT` | NULL | | 文本/HTML/LaTeX（block 主体） |
| `image_id` | `UUID` | NULL, FK → `document_parse_images(id)` ON DELETE SET NULL | | 仅 `block_type='image'` 时有值 |
| `block_data` | `JSONB` | NOT NULL | | ⭐ 整块原 JSON（兜底） |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | `NOW()` | 创建时间 |

## 索引

| 索引名 | 列 | 说明 |
|--------|-----|------|
| `PK_document_parse_blocks` | `id` | 主键 |
| `IX_document_parse_blocks_parse_id_page_id_sort_index` | `(parse_id, page_id, sort_index)` | 按页顺序读 |
| `IX_document_parse_blocks_block_type` | `block_type` | 按类型过滤 |
| `IX_document_parse_blocks_image_id` | `image_id` | 按图片关联查询 |

## 关键设计点

- **`block_data` 存整块原 JSON**：MinerU 升级加新字段时，**无需改 schema**，所有新字段都在 JSONB 里
- 经常查的字段（page、type、text）单独提出来建索引——查询性能 OK
- 不常查的字段（angle、formula_latex）放在 `block_data` 里——不占结构化存储空间
- `image_id` 软关联到 `document_parse_images`（SET NULL）——图片被删时 block 仍保留，但失去图片引用

## 填充流程

```
MinerU Worker 下载 ZIP
  ↓
解压 content_list.json（string）
  ↓
INSERT INTO document_parse_blocks
  对每个 block:
    parse_id       = 当前解析 ID
    page_id        = block.page_id
    sort_index     = 在 page 内的索引
    block_type     = block.type
    text_content   = block.text / block.content / block.body
    block_data     = 整块 JSON 字符串
    image_id       = (type=image 时) 根据 imageName 在已上传图片表中查 ID
```

## 特殊说明

- 与 `document_parse_images` 是 `block_type='image'` 时一对一关系，但不是 DB 外键强约束（用 `image_id` 软引用）
- 删除 `document_parse` 时本表通过 CASCADE 自动删除
