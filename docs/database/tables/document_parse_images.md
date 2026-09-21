# document_parse_images — 文档解析图片表

> 本文件是 `document_parse_images` 表的唯一事实源。

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | 图片唯一标识 |
| `parse_id` | `UUID` | NOT NULL, FK → `document_parses(id)` ON DELETE CASCADE | | 关联解析 |
| `image_name` | `VARCHAR(200)` | NOT NULL | | 图片文件名（存量：MinerU ZIP 内原始文件名；新：StructaDoc Asset 显示名） |
| `image_path` | `VARCHAR(500)` | NOT NULL | | 双语义：存量解析 = OSS 对象路径；新解析（parse 有 `structadoc_parse_run_id`）= StructaDoc Asset ID（Guid 字符串） |
| `content_type` | `VARCHAR(50)` | NOT NULL | `'image/jpeg'` | MIME 类型（image/jpeg 或 image/png） |

## 索引

| 索引名 | 列 | 说明 |
|--------|-----|------|
| `PK_document_parse_images` | `id` | 主键 |
| `IX_document_parse_images_parse_id` | `parse_id` | 按解析查所有图片 |

## 特殊说明

- **`image_name`**：保留解析产物中的原始文件名（如 `img_in_image_box_15_3.png`），便于调试和回溯，也是 Markdown 相对路径 `images/<name>` 的匹配键
- **`image_path`**：按所属 parse 判别——存量解析为 OSS 路径；新解析为 StructaDoc Asset ID，字节经代理端点 `GET /admin/document-parses/{parseId}/images/{imageId}/content` 从 StructaDoc 流式读取（ADR-0009）
- **与 `document_parse_blocks.image_id` 的关系**：当 `block_type='image'` 时，`document_parse_blocks.image_id` 指向本表
- 删除 `document_parse` 时通过 CASCADE 自动删除；删除 `document_parse_image` 时 `document_parse_blocks.image_id` 被 SET NULL
