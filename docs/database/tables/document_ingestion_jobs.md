# document_ingestion_jobs — 文档导入任务表

> 本文件是 `document_ingestion_jobs` 表的唯一事实源。

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | 任务唯一标识 |
| `document_id` | `UUID` | NOT NULL, FK → documents(id) | | 关联文档 |
| `status` | `VARCHAR(20)` | NOT NULL | `'pending'` | 任务状态 |
| `parser_version` | `VARCHAR(20)` | NULL | | 解析器版本 |
| `ocr_version` | `VARCHAR(20)` | NULL | | OCR 版本 |
| `error_message` | `TEXT` | NULL | | 失败原因 |
| `started_at` | `TIMESTAMP WITH TIME ZONE` | NULL | | 开始时间 |
| `finished_at` | `TIMESTAMP WITH TIME ZONE` | NULL | | 完成时间 |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | | 创建时间 |

## 索引

| 索引名 | 列 | 类型 | 说明 |
|--------|-----|------|------|
| `PK_document_ingestion_jobs` | `id` | PRIMARY KEY | 主键 |
| `IX_document_ingestion_jobs_status` | `status` | INDEX | 轮询 pending 任务 |

## 外键

| 约束名 | 列 | 引用 | 级联 |
|--------|-----|------|------|
| `FK_document_ingestion_jobs_document_document_id` | `document_id` | `documents(id)` | ON DELETE CASCADE |

## 特殊说明

- 状态流转：`pending` → `parsing` → `completed` / `failed`
- `IngestionWorker` 后台服务轮询 `pending` 状态的任务
- 删除文档级联删除关联的导入任务