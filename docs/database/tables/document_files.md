# document_files — 文档文件表

> 本文件是 `document_files` 表的唯一事实源。

## 设计背景

`document_files` 表存储文档解析流程的文件元数据，由 `StructaDocParseWorker` 驱动解析（ADR-0009）。新文档原件由 StructaDoc 主责存储，本表只保存 `structadoc_document_id` 引用；`file_path` 仅存量（迁移前上传）记录使用。

`document_files` 故意保持精简：只存储文件元数据，**所有解析相关字段**（status、markdown、task_id 等）都已拆到 `document_parses` 表。

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | 文件唯一标识 |
| `file_name` | `VARCHAR(500)` | NOT NULL | | 文件名 |
| `file_path` | `VARCHAR(500)` | NULL | | OSS 存储路径；仅存量记录（迁移前上传的原文件），新记录为 NULL |
| `structadoc_document_id` | `UUID` | NULL | | StructaDoc Document ID（新上传必填；存量文件在首次触发解析时惰性上传后回填） |
| `content_type` | `VARCHAR(100)` | NOT NULL | | MIME 类型（如 `application/pdf`） |
| `created_by` | `UUID` | NULL | | 上传者用户 ID |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | | 创建时间 |
| `updated_at` | `TIMESTAMP WITH TIME ZONE` | NULL | | 最后更新时间 |
| `subject` | `VARCHAR(50)` | NULL | | 学科元数据（English/语文/数学/物理/化学/生物/其他）。可由 `PUT /admin/document-files/{id}/metadata` 手动设置，或在解析完成后由 LLM 自动填充缺失字段（best-effort）。详见 [DocumentMetadataAnalysis 模块](../../modules/DocumentMetadataAnalysis/01-FEATURE.md) |
| `grade` | `VARCHAR(20)` | NULL | | 年级元数据（K/G1-G12）。来源同 `subject` |
| `year` | `VARCHAR(10)` | NULL | | 年份元数据（4 位数字，如 2024）。来源同 `subject` |

## 索引

| 索引名 | 列 | 说明 |
|--------|-----|------|
| `PK_document_files` | `id` | 主键 |
| `IX_document_files_file_name` | `file_name` | 按文件名查询 |

## 外键

- 引用：本表是 `document_parses.document_file_id` 的目标

## 特殊说明

- **OSS 路径规范（仅存量）**：`documents/{year}/{month}/{day}/{file-id}/source.{ext}`
  - 同目录下还会存：`mineru-output.zip`、`layout.pdf`、`images/*`
  - 删除文件时整个目录一起清理
- `file_path` 与 `structadoc_document_id` 至少其一非空；删除时分别 best-effort 清理 OSS 对象与 StructaDoc 文档
