# 01-FEATURE — MinerU 文档解析（持久化版）

## 功能名称

**MinerU Document Parsing (Persistent)** — MinerU 文档解析集成（持久化版）

## 功能概述

本模块将 MinerU 文档解析流程从"前端驱动的一次性 demo"升级为"后端驱动的持久化流程"：

1. **文件上传**：用户选择文件上传到 OSS，数据库记录文件路径、上传日期、上传人。此步骤不触发任何解析。
2. **MinerU 解析**：用户对已上传文件触发 MinerU 解析，后端提交任务到 MinerU API，后台轮询状态，解析完成后将 Markdown 和图片持久化（MD 入库、图片入 OSS）。支持 vlm 和 pipeline 两种模型版本，同一文件可同时拥有两种解析结果。
3. **查看 MD 文档**：管理端选择文件，展示完整的 Markdown 渲染结果（含引用图片）。

整个流程状态持久化在数据库中，前端无状态，刷新安全。

## 单一用户故事

> **作为** DocLibrary 管理员，
> **我希望** 先上传文件到系统，再对文件触发 MinerU 解析，解析结果持久化保存，
> **以便** 我可以随时查看解析后的 Markdown 文档（含图片），且刷新页面不会丢失任何进度或结果。

## 验收条件

| # | 验收条件 | 验证方式 |
|---|---------|---------|
| AC-1 | 管理页面可上传文件（PDF/DOC/DOCX/PPT/PPTX，≤200MB），上传后文件存入 OSS，数据库记录文件信息 | API + 手动测试 |
| AC-2 | 文件列表展示已上传文件，显示文件名、上传时间、上传人、解析状态 | 手动测试 |
| AC-3 | 对未解析文件可触发 MinerU 解析（vlm 或 pipeline），后端提交任务到 MinerU API | API 测试 |
| AC-4 | 解析状态持久化：刷新页面后状态不丢失，可继续查看进度 | 手动测试 |
| AC-5 | 解析完成后 Markdown 内容入库，图片上传到 OSS，MD 中图片路径替换为 OSS 路径，查看时替换为 presigned URL | API 测试 |
| AC-6 | 管理页面可选择文件并展示完整 Markdown 渲染（含图片） | 手动测试 |
| AC-7 | 支持同时解析多个文件（并发安全） | 并发测试 |
| AC-8 | 解析失败时显示错误信息，支持重试 | 模拟异常 |
| AC-9 | 文件超过 200MB 时提示限制 | 手动测试 |
| AC-10 | Token 未配置时提示功能不可用 | 手动测试 |
| AC-11 | 同一文件可同时拥有 vlm 和 pipeline 两种解析结果 | API 测试 |
| AC-12 | 大文档（>200 页）自动拆分为多个子文档分别解析，然后合并结果 | API 测试 |
| AC-13 | 非 PDF 文件（DOCX/PPTX）自动通过 LibreOffice 转换为 PDF 后解析 | API 测试 |

## 范围内

- 文件上传 API（上传到 OSS + 入库）
- 文件列表 API（分页查询 + parseStatus 过滤 + 文件名搜索）
- MinerU 解析触发 API（后台提交 + 轮询 + 结果持久化，支持 vlm/pipeline）
- 解析状态查询 API（文件详情含所有解析记录）
- Markdown 文档查看 API（含图片 presigned URL）
- 管理页面文件上传/列表/解析/查看
- 图片上传到自有 OSS 并替换 Markdown 路径
- 大文档自动拆分（每 200 页）和合并
- 非 PDF 文件自动转换（LibreOffice headless）
- 导出：MD+图片 ZIP / 自包含 HTML

## 范围外

- 解析结果接入分段/索引流程（由 OpenSearchBlockIndexing 模块负责）
- Batch 批量文件解析（后续优化）
- Agent Lightweight API 集成（后续优化）

## 流程设计

### 阶段一：文件上传

```
用户选择文件 → POST /admin/document-files/upload
  → 文件上传到 OSS (documents/doclibrary-files/{Guid}{ext})
  → 数据库写入 document_files 记录 (无初始状态字段)
  → 返回 fileId
```

### 阶段二：MinerU 解析

```
用户点击"解析" → POST /admin/document-files/{id}/parse?modelVersion=vlm
  → 新建 document_parses 记录 (model_version='vlm', status=pending)
  → MinerUFileParseWorker 检测到 pending 任务
  → 更新 status=parsing
  → 下载源文件 → [非 PDF 则 LibreOffice 转换]
  → 检测页数 → [>200 页则拆分]
  → 生成 presigned URL → 提交 MinerU → 获取 task_id
  → 轮询 MinerU 状态
  → 完成后下载 ZIP → 提取 MD + 图片 + content_list + 其他 JSON
  → 图片上传 OSS → 替换 MD 路径
  → 写入 document_parse_images
  → 解析 content_list.json → 写入 document_parse_blocks
  → 更新 document_parses (status=parsed, markdown_content, content_list, content_list_v2, model_json, layout_json, zip_path)
  → 索引 blocks 到 OpenSearch（best-effort）
  → LLM 分析元数据（best-effort）
```

### 阶段三：查看 MD

```
用户点击文件 → GET /admin/document-files/{id}
  → 返回文件信息 + 所有解析记录（含 markdown_content）
  → 图片路径已为 OSS presigned URL
  → 前端渲染 Markdown（含图片）
```

## 数据模型

### document_files 表

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | 文件唯一标识 |
| `file_name` | `VARCHAR(500)` | NOT NULL | | 原始文件名 |
| `file_path` | `VARCHAR(500)` | NOT NULL | | OSS 存储路径（用户上传的原文件） |
| `content_type` | `VARCHAR(100)` | NOT NULL | | MIME 类型 |
| `created_by` | `UUID` | NULL | | 上传者用户 ID（当前为 null，内网管理后台无认证） |
| `subject` | `VARCHAR(50)` | NULL | | 学科元数据（English/语文/数学/物理/化学/生物/其他）。可由 `PUT /admin/document-files/{id}/metadata` 手动设置，或在 MinerU 解析完成后由 LLM 自动填充（best-effort）。详见 [DocumentMetadataAnalysis 模块](../DocumentMetadataAnalysis/01-FEATURE.md) |
| `grade` | `VARCHAR(20)` | NULL | | 年级元数据（K/G1-G12）。来源同 `subject` |
| `year` | `VARCHAR(10)` | NULL | | 年份元数据（4 位数字，如 2024）。来源同 `subject` |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | `NOW()` | 创建时间 |
| `updated_at` | `TIMESTAMP WITH TIME ZONE` | NULL | | 最后更新时间（触发器自动维护） |

### document_parses 表

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | 解析记录唯一标识 |
| `document_file_id` | `UUID` | NOT NULL, FK → `document_files(id)` ON DELETE CASCADE | | 关联文件 |
| `model_version` | `VARCHAR(20)` | NOT NULL | `'vlm'` | 解析模型版本：`vlm` / `pipeline` |
| `status` | `VARCHAR(30)` | NOT NULL | `'pending'` | 解析状态：`pending` / `parsing` / `parsed` / `failed` |
| `external_task_id` | `VARCHAR(100)` | NULL | | MinerU 任务 ID（异步模式下用于轮询） |
| `markdown_content` | `TEXT` | NULL | | 从 MinerU ZIP 抽取的 `full.md` 内容 |
| `content_list` | `JSONB` (PG) / `TEXT` (SQLite) | NULL | | 完整 `content_list.json`（结构化 block 数组 v1） |
| `content_list_v2` | `JSONB` (PG) / `TEXT` (SQLite) | NULL | | 结构化 block 数组 v2（MinerU 升级后新增，字段更丰富） |
| `model_json` | `JSONB` (PG) / `TEXT` (SQLite) | NULL | | 模型推理结果（含 bbox 坐标、版面分类） |
| `layout_json` | `JSONB` (PG) / `TEXT` (SQLite) | NULL | | 版面分析数据（含每页 bbox 坐标） |
| `zip_path` | `VARCHAR(500)` | NULL | | 完整 MinerU ZIP 在 OSS 的路径 |
| `error_message` | `TEXT` | NULL | | 解析失败时的错误信息 |
| `parsed_at` | `TIMESTAMP WITH TIME ZONE` | NULL | | 解析完成时间 |

### document_parse_blocks 表

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | block 唯一标识 |
| `parse_id` | `UUID` | NOT NULL, FK → `document_parses(id)` ON DELETE CASCADE | | 关联解析 |
| `page_id` | `INT` | NOT NULL | | 所属页（0-indexed） |
| `sort_index` | `INT` | NOT NULL | | 在 page 内的阅读顺序 |
| `block_type` | `VARCHAR(20)` | NOT NULL | | 类型：`text` / `table` / `equation` / `image` / `code` / `list` 等 |
| `text_content` | `TEXT` | NULL | | 文本/HTML/LaTeX（block 主体） |
| `image_id` | `UUID` | NULL, FK → `document_parse_images(id)` ON DELETE SET NULL | | 仅 `block_type='image'` 时有值 |
| `block_data` | `JSONB` (PG) / `TEXT` (SQLite) | NOT NULL | `'{}'` | 整块原 JSON（兜底，MinerU 升级无需改 schema） |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | `NOW()` | 创建时间 |

### document_parse_images 表

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | 图片唯一标识 |
| `parse_id` | `UUID` | NOT NULL, FK → `document_parses(id)` ON DELETE CASCADE | | 关联解析 |
| `image_name` | `VARCHAR(200)` | NOT NULL | | MinerU ZIP 中的原始文件名 |
| `image_path` | `VARCHAR(500)` | NOT NULL | | OSS 存储路径 |
| `content_type` | `VARCHAR(50)` | NOT NULL | `'image/jpeg'` | MIME 类型（image/jpeg 或 image/png） |

### document_parse_imports 表

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | 主键 |
| `parse_id` | `UUID` | NOT NULL, FK → `document_parses(id)` ON DELETE CASCADE, UNIQUE | | 关联 parse（一个 parse 至多一条导入记录） |
| `imported_by` | `UUID` | NOT NULL | | 导入操作者（QuestionBank 服务账号 ID） |
| `status` | `VARCHAR(20)` | NOT NULL | | `imported` / `failed` |
| `note` | `TEXT` | NULL | | 备注（如失败原因、导入题目数等） |
| `imported_question_ids` | `TEXT` | NULL | | 导入的 QuestionBank 题目 ID 列表（JSON 数组字符串） |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | `NOW()` | 创建时间 |
| `updated_at` | `TIMESTAMP WITH TIME ZONE` | NULL | | 最后更新时间 |

## 解析状态流转

```
触发解析 ──▶ pending ──Worker开始──▶ parsing ──成功──▶ parsed
                                      │
                                      │ 失败
                                      ▼
                                    failed (可重试，新建记录)
```

## 配置项

```json
{
  "MinerU": {
    "ApiToken": "eyJ0eXAi...",
    "BaseUrl": "https://mineru.net",
    "ModelVersion": "vlm"
  }
}
```

## 关键代码参考

| 组件 | 文件路径 |
|------|---------|
| MinerU Precision 客户端 | `src/Service/MinerUPrecisionClient.cs` |
| 后台解析 Worker | `src/Service/MinerUFileParseWorker.cs` |
| PDF 拆分服务 | `src/Service/PdfSplitService.cs` |
| LibreOffice 转换服务 | `src/Service/LibreOfficeConversionService.cs` |
| 文件管理端点 | `src/Service/Endpoints/DocumentFileEndpoints.cs` |
| 解析记录端点 | `src/Service/Endpoints/DocumentParseEndpoints.cs` |
| 导出端点 | `src/Service/Endpoints/DocumentExportEndpoints.cs` |
| 前端页面 | `frontend/src/views/DocManagePage.vue`、`ParseResultsPage.vue` |

## 文档索引

| 文档 | 说明 |
|------|------|
| [01-FEATURE.md](./01-FEATURE.md) | 功能概述、用户故事、验收条件（本文档） |
| [02-SPEC.md](./02-SPEC.md) | 详细需求规格、验收场景、非功能需求、测试策略 |
| [03-DESIGN.md](./03-DESIGN.md) | 文件结构、接口签名、数据流、错误处理、外部依赖 |
