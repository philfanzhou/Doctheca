# document_parses — 文档解析记录表

> 本文件是 `document_parses` 表的唯一事实源。

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | 解析记录唯一标识 |
| `document_file_id` | `UUID` | NOT NULL, FK → `document_files(id)` ON DELETE CASCADE | | 关联文件 |
| `model_version` | `VARCHAR(20)` | NOT NULL | `'vlm'` | 解析模型版本：`vlm` / `pipeline` |
| `status` | `VARCHAR(30)` | NOT NULL | `'pending'` | 解析状态：`pending` / `parsing` / `parsed` / `failed` |
| `external_task_id` | `VARCHAR(100)` | NULL | | 外部解析任务 ID：存量为 MinerU task_id，新记录为 StructaDoc Parse Run ID |
| `structadoc_parse_run_id` | `UUID` | NULL | | StructaDoc Parse Run ID；非空即新管线记录（决定图片/产物的读取路径） |
| `markdown_content` | `TEXT` | NULL | | 解析产物 Markdown（存量：MinerU `full.md`；新：StructaDoc canonical Markdown Artifact） |
| `content_list` | `JSONB` | NULL | | 完整 `content_list.json`（结构化 block 数组 v1）；**存量只读列**，新管线不写入 |
| `content_list_v2` | `JSONB` | NULL | | 结构化 block 数组 v2；**存量只读列**，新管线不写入 |
| `model_json` | `JSONB` | NULL | | 模型推理结果（含 bbox 坐标、版面分类）；**存量只读列** |
| `layout_json` | `JSONB` | NULL | | 版面分析数据（含每页 bbox 坐标）；**存量只读列** |
| `zip_path` | `VARCHAR(500)` | NULL | | 完整 MinerU ZIP 在 OSS 的路径；**存量只读列**（新解析产物由 StructaDoc 主责存储） |
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
- **`content_list_v2` (jsonb)**：存量的 structured block 数组 v2（含 bbox / 分类等），与 v1 并存；新管线不写入。
- **`model_json` / `layout_json` (jsonb)**：存量模型推理与版面分析数据；新管线不写入。
- **`zip_path` (varchar)**：存量完整 ZIP 在 OSS 的路径，**兜底**；新解析的原始产物（provider-archive 等 Artifact）保存在 StructaDoc，经其 API 读取。
- **新管线数据面**：blocks/images 本地同步副本照常写入 `document_parse_blocks` / `document_parse_images`，Markdown 写 `markdown_content`，详见 [DocumentParse 能力文档](../../modules/DocumentParse.md)。

## 特殊说明

- **多模型版本并存**：同一个 `document_file_id` 可以同时拥有 `vlm` 和 `pipeline` 两种解析记录，互不影响。重新解析时创建新记录，旧记录保留。
- 删除 `document_file` 时，所有关联的 `document_parses` 记录通过 CASCADE 自动删除，同时 `document_parse_blocks` 和 `document_parse_images` 也被 CASCADE 删除
