# document_parses — 文档解析记录表

> 本文件是 `document_parses` 表的唯一事实源。

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | 解析记录唯一标识 |
| `document_file_id` | `UUID` | NOT NULL, FK → `document_files(id)` ON DELETE CASCADE | | 关联文件 |
| `model_version` | `VARCHAR(20)` | NOT NULL | `'vlm'` | 解析模型版本：`vlm` / `pipeline` |
| `status` | `VARCHAR(30)` | NOT NULL | `'pending'` | 解析状态：`pending` / `parsing` / `parsed` / `failed` |
| `external_task_id` | `VARCHAR(100)` | NULL | | MinerU 任务 ID（异步模式下用于轮询） |
| `markdown_content` | `TEXT` | NULL | | 从 MinerU ZIP 抽取的 `full.md` 内容 |
| `content_list` | `JSONB` | NULL | | 完整 `content_list.json`（结构化 block 数组 v1） |
| `content_list_v2` | `JSONB` | NULL | | 结构化 block 数组 v2（MinerU 升级后新增，字段更丰富） |
| `model_json` | `JSONB` | NULL | | 模型推理结果（含 bbox 坐标、版面分类） |
| `layout_json` | `JSONB` | NULL | | 版面分析数据（含每页 bbox 坐标） |
| `zip_path` | `VARCHAR(500)` | NULL | | 完整 MinerU ZIP 在 OSS 的路径 |
| `error_message` | `TEXT` | NULL | | 解析失败时的错误信息 |
| `parsed_at` | `TIMESTAMP WITH TIME ZONE` | NULL | | 解析完成时间 |

## 索引

| 索引名 | 列 | 说明 |
|--------|-----|------|
| `PK_document_parses` | `id` | 主键 |
| `IX_document_parses_document_file_id` | `document_file_id` | 按文件查最新解析 |
| `IX_document_parses_status` | `status` | 按状态筛选（轮询 pending 用） |
| `IX_document_parses_model_version` | `model_version` | 按模型版本查询 |

## 存储策略

- **`markdown_content` (text)**：直接展示用，单文档可达 5MB+
- **`content_list_v2` (jsonb)**：MinerU 升级后新增的 structured block 数组 v2，字段更丰富（含 bbox / 分类等），与 v1 并存。
- **`model_json` (jsonb)**：模型推理结果（含 bbox 坐标、版面分类、置信度）。
- **`layout_json` (jsonb)**：版面分析数据（含每页 bbox 坐标和分类）。
- **`zip_path` (varchar)**：完整 ZIP 在 OSS 的路径，**兜底**。如果未来 MinerU 升级导致 JSON 格式变化，可以从 ZIP 重新抽取。

## 特殊说明

- **多模型版本并存**：同一个 `document_file_id` 可以同时拥有 `vlm` 和 `pipeline` 两种解析记录，互不影响。重新解析时创建新记录，旧记录保留。
- 删除 `document_file` 时，所有关联的 `document_parses` 记录通过 CASCADE 自动删除，同时 `document_parse_blocks` 和 `document_parse_images` 也被 CASCADE 删除
