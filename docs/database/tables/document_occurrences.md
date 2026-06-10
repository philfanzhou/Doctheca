# document_occurrences — 词元出现位置表

> 本文件是 `document_occurrences` 表的唯一事实源。

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | 唯一标识 |
| `document_id` | `UUID` | NOT NULL, FK → documents(id) | | 所属文档 |
| `segment_id` | `UUID` | NULL, FK → document_segments(id) | | 所属文本片段 |
| `question_segment_id` | `UUID` | NULL, FK → question_segments(id) | | 所属题目片段 |
| `token_text` | `VARCHAR(200)` | NOT NULL | | 词元原始文本 |
| `token_stem` | `VARCHAR(200)` | NOT NULL | | 词元词干（stemming 后） |
| `start_offset` | `INT` | NOT NULL | | 起始偏移量 |
| `end_offset` | `INT` | NOT NULL | | 结束偏移量 |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | | 创建时间 |

## 索引

| 索引名 | 列 | 类型 | 说明 |
|--------|-----|------|------|
| `PK_document_occurrences` | `id` | PRIMARY KEY | 主键 |
| `IX_document_occurrences_document_id_token_text` | `document_id, token_text` | INDEX | 按文本精确搜索 |
| `IX_document_occurrences_document_id_token_stem` | `document_id, token_stem` | INDEX | 按词干搜索 |

## 外键

| 约束名 | 列 | 引用 | 级联 |
|--------|-----|------|------|
| `FK_document_occurrences_document_document_id` | `document_id` | `documents(id)` | ON DELETE CASCADE |
| `FK_document_occurrences_segment_segment_id` | `segment_id` | `document_segments(id)` | ON DELETE SET NULL |
| `FK_document_occurrences_question_question_segment_id` | `question_segment_id` | `question_segments(id)` | ON DELETE SET NULL |

## 特殊说明

- 同一 token 可能同时有 `segment_id` 和 `question_segment_id`（题目中的 token 也是 segment）
- `token_stem` 用于词干搜索（英语 stemming 还原为原型）
- `token_text` 用于精确匹配搜索
- 这是倒排索引的数据库实现，支持全文搜索