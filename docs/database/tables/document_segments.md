# document_segments — 文档文本片段表

> 本文件是 `document_segments` 表的唯一事实源。

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | 片段唯一标识 |
| `document_id` | `UUID` | NOT NULL, FK → documents(id) | | 所属文档 |
| `page_id` | `UUID` | NOT NULL, FK → document_pages(id) | | 所在页面 |
| `block_id` | `VARCHAR(50)` | NOT NULL | | 逻辑块 ID（段落级） |
| `sentence_id` | `VARCHAR(50)` | NOT NULL | | 句子 ID |
| `segment_type` | `VARCHAR(20)` | NOT NULL | `'sentence'` | 片段类型 |
| `text` | `TEXT` | NOT NULL | | 文本内容 |
| `start_offset` | `INT` | NOT NULL | | 起始偏移量 |
| `end_offset` | `INT` | NOT NULL | | 结束偏移量 |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | | 创建时间 |

## 索引

| 索引名 | 列 | 类型 | 说明 |
|--------|-----|------|------|
| `PK_document_segments` | `id` | PRIMARY KEY | 主键 |
| `IX_document_segments_document_id_sentence_id` | `document_id, sentence_id` | UNIQUE INDEX | 同文档内句子唯一 |

## 外键

| 约束名 | 列 | 引用 | 级联 |
|--------|-----|------|------|
| `FK_document_segments_document_document_id` | `document_id` | `documents(id)` | ON DELETE CASCADE |
| `FK_document_segments_page_page_id` | `page_id` | `document_pages(id)` | ON DELETE CASCADE |

## 特殊说明

- 文本片段是文档解析后按句子粒度拆分的结果
- 每个 segment 的 word tokens 存储在 `document_occurrences` 表中
- 删除文档或页面均级联删除关联片段