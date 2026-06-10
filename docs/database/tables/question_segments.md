# question_segments — 题目片段表

> 本文件是 `question_segments` 表的唯一事实源。

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | 题目唯一标识 |
| `document_id` | `UUID` | NOT NULL, FK → documents(id) | | 所属文档 |
| `page_id` | `UUID` | NOT NULL, FK → document_pages(id) | | 所在页面 |
| `question_id` | `VARCHAR(50)` | NOT NULL | | 题目编号 |
| `stem` | `TEXT` | NOT NULL | | 题目主干 |
| `options_json` | `TEXT` | NULL | | 选项 JSON |
| `answer_area` | `TEXT` | NULL | | 答题区域信息 |
| `start_offset` | `INT` | NOT NULL | | 起始偏移量 |
| `end_offset` | `INT` | NOT NULL | | 结束偏移量 |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | | 创建时间 |

## 索引

| 索引名 | 列 | 类型 | 说明 |
|--------|-----|------|------|
| `PK_question_segments` | `id` | PRIMARY KEY | 主键 |
| `IX_question_segments_document_id_question_id` | `document_id, question_id` | UNIQUE INDEX | 同文档内题目唯一 |

## 外键

| 约束名 | 列 | 引用 | 级联 |
|--------|-----|------|------|
| `FK_question_segments_document_document_id` | `document_id` | `documents(id)` | ON DELETE CASCADE |
| `FK_question_segments_page_page_id` | `page_id` | `document_pages(id)` | ON DELETE CASCADE |

## 特殊说明

- 题目是文档解析时从 PDF/DOCX 中提取的选择题/填空题
- `options_json` 存储 JSON 格式的选项数据
- 题目的 word tokens 也存储在 `document_occurrences`（通过 `question_segment_id` 关联）