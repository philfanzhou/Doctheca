# documents — 文档表

> 本文件是 `documents` 表的唯一事实源。其他文档如需引用表结构，请链接至此。

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | 文档唯一标识 |
| `title` | `VARCHAR(200)` | NOT NULL | | 文档标题 |
| `source_type` | `VARCHAR(20)` | NOT NULL | | 来源文件类型（如 `pdf`, `word`, `ppt`） |
| `file_hash` | `VARCHAR(64)` | NOT NULL | | 文件 SHA256 哈希，用于去重 |
| `file_path` | `VARCHAR(500)` | NOT NULL | | OSS 存储路径 |
| `file_size` | `BIGINT` | NOT NULL | | 文件大小（字节） |
| `language` | `VARCHAR(10)` | NOT NULL | `'en'` | 文档语言 |
| `grade` | `VARCHAR(20)` | NOT NULL | | 年级（K, G1-G12） |
| `subject` | `VARCHAR(20)` | NOT NULL | | 学科 |
| `year` | `VARCHAR(10)` | NOT NULL | | 年份 |
| `tags` | `TEXT` | NULL | | 标签，逗号分隔 |
| `status` | `VARCHAR(20)` | NOT NULL | `'pending'` | 文档状态：`pending` → `processing` → `ready` / `failed` |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | | 创建时间 |
| `updated_at` | `TIMESTAMP WITH TIME ZONE` | NULL | | 最后更新时间 |

## 索引

| 索引名 | 列 | 类型 | 说明 |
|--------|-----|------|------|
| `PK_documents` | `id` | PRIMARY KEY | 主键 |
| `IX_documents_title` | `title` | UNIQUE INDEX | 标题唯一 |
| `IX_documents_file_hash` | `file_hash` | INDEX | 按哈希查重 |
| `IX_documents_subject_grade_year` | `subject, grade, year` | INDEX | 搜索过滤 |
| `IX_documents_status` | `status` | INDEX | 按状态筛选 |

## 外键

无外键（根表，被其他表引用）。

## 特殊说明

- `status` 状态流转：`pending`（待解析）→ `processing`（处理中）→ `ready`（可搜索）/ `failed`（失败）
- `title` 唯一约束：同一标题不允许重复上传
- `file_hash` 用于去重检测，上传前检查是否已有相同哈希的 ready 状态文档