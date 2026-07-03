# document_parses — 文档解析记录表

> 本文件是 `document_parses` 表的唯一事实源。

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | 解析记录唯一标识 |
| `document_file_id` | `UUID` | NOT NULL, FK → `document_files(id)` ON DELETE CASCADE | | 关联文件 |
| `status` | `VARCHAR(30)` | NOT NULL | `'pending'` | 解析状态：`pending` / `parsing` / `parsed` / `failed` |
| `external_task_id` | `VARCHAR(100)` | NULL | | MinerU 任务 ID（异步模式下用于轮询） |
| `markdown_content` | `TEXT` | NULL | | 从 MinerU ZIP 抽取的 `full.md` 内容 |
| `content_list` | `TEXT` (SQLite) / `JSONB` (PostgreSQL) | NULL | | 完整 `content_list.json`（结构化 block 数组） |
| `zip_path` | `VARCHAR(500)` | NULL | | 完整 MinerU ZIP 在 OSS 的路径 |
| `error_message` | `TEXT` | NULL | | 解析失败时的错误信息 |
| `parsed_at` | `TIMESTAMP WITH TIME ZONE` | NULL | | 解析完成时间 |

## 索引

| 索引名 | 列 | 说明 |
|--------|-----|------|
| `PK_document_parses` | `id` | 主键 |
| `IX_document_parses_document_file_id` | `document_file_id` | 按文件查最新解析 |
| `IX_document_parses_status` | `status` | 按状态筛选（轮询 pending 用） |

## 存储策略

- **`markdown_content` (text)**：直接展示用，单文档可达 5MB+
- **`content_list` (jsonb on PG / text on SQLite)**：**核心结构化数据**。MinerU 输出的 block 数组（含 bbox / type / text / page_id），用于：
  - 按页查询
  - 按 block_type 过滤
  - 程序化分析
- **`zip_path` (varchar)**：完整 ZIP 在 OSS 的路径，**兜底**。如果未来 MinerU 升级导致 `content_list.json` 格式变化，可以从 ZIP 重新抽取。
- **`middle.json` / `model.json` 不存**：低分析价值。包含在 ZIP 内，需要时从 ZIP 抽取。

## 特殊说明

- **覆盖策略（approach A）**：同一个 `document_file_id` 重复解析时，旧的 `document_parses` 记录**保留**（不级联删除），新记录覆盖 status / markdown / content_list / zip_path
- 删除 `document_file` 时，所有关联的 `document_parses` 记录通过 CASCADE 自动删除，同时 `document_parse_blocks` 和 `document_parse_images` 也被 CASCADE 删除
