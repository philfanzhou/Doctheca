# 01-FEATURE — MinerU 文档解析（持久化版）

## 功能名称

**MinerU Document Parsing (Persistent)** — MinerU 文档解析集成（持久化版）

## 功能概述

本模块将 MinerU 文档解析流程从"前端驱动的一次性 demo"升级为"后端驱动的持久化流程"：

1. **文件上传**：用户选择文件上传到 S3，数据库记录文件路径、上传日期、上传人。此步骤不触发任何解析。
2. **MinerU 解析**：用户对已上传文件触发 MinerU 解析，后端提交任务到 MinerU API，后台轮询状态，解析完成后将 Markdown 和图片持久化（MD 入库、图片入 S3）。
3. **查看 MD 文档**：管理端选择文件，展示完整的 Markdown 渲染结果（含引用图片）。

整个流程刷新安全：所有状态持久化在数据库中，前端无状态。

## 单一用户故事

> **作为** DocRetrieval 管理员，
> **我希望** 先上传文件到系统，再对文件触发 MinerU 解析，解析结果持久化保存，
> **以便** 我可以随时查看解析后的 Markdown 文档（含图片），且刷新页面不会丢失任何进度或结果。

## 验收条件

| # | 验收条件 | 验证方式 |
|---|---------|---------|
| AC-1 | 管理页面可上传文件（PDF/DOCX/PPTX，≤200MB），上传后文件存入 S3，数据库记录文件信息 | API + 手动测试 |
| AC-2 | 文件列表展示已上传文件，显示文件名、上传时间、上传人、解析状态 | 手动测试 |
| AC-3 | 对未解析文件可触发 MinerU 解析，后端提交任务到 MinerU API | API 测试 |
| AC-4 | 解析状态持久化：刷新页面后状态不丢失，可继续查看进度 | 手动测试 |
| AC-5 | 解析完成后 Markdown 内容入库，图片上传到 S3，MD 中图片路径替换为 S3 路径，查看时替换为 presigned URL | API 测试 |
| AC-6 | 管理页面可选择文件并展示完整 Markdown 渲染（含图片） | 手动测试 |
| AC-7 | 支持同时解析多个文件（并发安全） | 并发测试 |
| AC-8 | 解析失败时显示错误信息，支持重试 | 模拟异常 |
| AC-9 | 文件超过 200MB 时提示限制 | 手动测试 |
| AC-10 | Token 未配置时提示功能不可用 | 手动测试 |

## 范围内

- 文件上传 API（上传到 S3 + 入库）
- 文件列表 API（分页查询）
- MinerU 解析触发 API（后台提交 + 轮询 + 结果持久化）
- 解析状态查询 API
- Markdown 文档查看 API（含图片 presigned URL）
- 管理页面文件上传/列表/解析/查看
- 图片上传到自有 S3 并替换 Markdown 路径

## 范围外

- 解析结果接入分段/索引流程（后续优化）
- 替代现有 DocumentParserService（后续优化）
- Batch 批量文件解析（后续优化）
- Agent Lightweight API 集成（后续优化）

## 流程设计

### 阶段一：文件上传

```
用户选择文件 → POST /admin/document-files/upload
  → 文件上传到 S3 (documents/docretrieval-files/{Guid}{ext})
  → 数据库写入 document_files 记录 (status=uploaded)
  → 返回 fileId
```

### 阶段二：MinerU 解析

```
用户点击"解析" → POST /admin/document-files/{id}/parse
  → 更新 status=pending_parse
  → IngestionWorker 检测到 pending_parse 任务
  → 生成 presigned URL → 提交 MinerU API → 获取 task_id
  → 更新 status=parsing, external_task_id=task_id
  → 后台轮询 MinerU 状态
  → 完成后下载 ZIP → 提取 MD + 图片
  → 图片上传 S3 → 替换 MD 路径
  → MD 内容写入 document_files.markdown_content
  → 图片路径写入 document_file_images 表
  → 更新 status=parsed
```

### 阶段三：查看 MD

```
用户点击文件 → GET /admin/document-files/{id}
  → 返回文件信息 + markdown_content
  → 图片路径已为 S3 presigned URL
  → 前端渲染 Markdown（含图片）
```

## 数据模型

### document_files 表

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PK | | 文件唯一标识 |
| `file_name` | `VARCHAR(500)` | NOT NULL | | 原始文件名 |
| `file_path` | `VARCHAR(500)` | NOT NULL | | S3 存储路径 |
| `file_size` | `BIGINT` | NOT NULL | | 文件大小（字节） |
| `content_type` | `VARCHAR(100)` | NOT NULL | | MIME 类型 |
| `status` | `VARCHAR(30)` | NOT NULL | `'uploaded'` | 文件状态 |
| `external_task_id` | `VARCHAR(100)` | NULL | | MinerU 任务 ID |
| `markdown_content` | `TEXT` | NULL | | 解析后的 Markdown 内容 |
| `error_message` | `TEXT` | NULL | | 解析错误信息 |
| `parsed_at` | `TIMESTAMPTZ` | NULL | | 解析完成时间 |
| `created_by` | `UUID` | NULL | | 上传者用户 ID |
| `created_at` | `TIMESTAMPTZ` | NOT NULL | | 上传时间 |
| `updated_at` | `TIMESTAMPTZ` | NULL | | 最后更新时间 |

状态流转：`uploaded` → `pending_parse` → `parsing` → `parsed` / `parse_failed`

### document_file_images 表

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PK | | 图片唯一标识 |
| `document_file_id` | `UUID` | FK | | 关联文件 ID |
| `image_name` | `VARCHAR(200)` | NOT NULL | | 原始图片文件名 |
| `image_path` | `VARCHAR(500)` | NOT NULL | | S3 存储路径 |
| `content_type` | `VARCHAR(50)` | NOT NULL | `'image/jpeg'` | 图片 MIME 类型 |
| `file_size` | `BIGINT` | NOT NULL | | 图片大小 |
| `created_at` | `TIMESTAMPTZ` | NOT NULL | | 创建时间 |

## 配置项

```json
{
  "MinerU": {
    "ApiToken": "eyJ0eXAi...",
    "BaseUrl": "https://mineru.net"
  }
}
```

## 关键代码参考

| 组件 | 文件路径 |
|------|---------|
| MinerU Precision 客户端 | `src/Service/MinerUPrecisionClient.cs` |
| Admin 端点 | `src/Service/DocumentAdminEndpoints.cs` |
| 前端页面 | `frontend/src/App.vue` |
