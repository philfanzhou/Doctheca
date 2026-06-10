# document_pages — 文档页面表

> 本文件是 `document_pages` 表的唯一事实源。

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | 页面唯一标识 |
| `document_id` | `UUID` | NOT NULL, FK → documents(id) | | 所属文档 |
| `page_number` | `INT` | NOT NULL | | 页码（从 1 开始） |
| `image_path` | `VARCHAR(500)` | NULL | | OSS 上该页截图路径 |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | | 创建时间 |

## 索引

| 索引名 | 列 | 类型 | 说明 |
|--------|-----|------|------|
| `PK_document_pages` | `id` | PRIMARY KEY | 主键 |
| `IX_document_pages_document_id_page_number` | `document_id, page_number` | UNIQUE INDEX | 同文档内页码唯一 |

## 外键

| 约束名 | 列 | 引用 | 级联 |
|--------|-----|------|------|
| `FK_document_pages_document_document_id` | `document_id` | `documents(id)` | ON DELETE CASCADE |

## 特殊说明

- 删除文档会级联删除所有页面
- `page_number` 在同一 `document_id` 下唯一