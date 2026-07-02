# MinerU 文档解析（持久化版）— 详细需求规格 (SPEC)

## 功能概述

本模块实现文件管理与文档解析两个独立业务：文件上传存储 → 触发 MinerU 解析 → Markdown 查看/导出。文件与解析解耦，文件可独立存在，解析为可选操作。所有状态持久化在数据库中，前端无状态，刷新安全。

## 前端菜单结构

| 菜单 | Key | 功能 |
|------|-----|------|
| 文件管理 | files | 上传文件到 S3、查看所有文档、删除文件（含关联解析和图片） |
| MinerU 解析 | parses | 查看未解析文档（默认）、触发解析、查看解析状态、强制重新解析 |
| Markdown 数据 | markdown | 查看所有解析记录、按文档名搜索、删除解析记录（仅删解析和图片，不删原始文件）、导出 HTML/MD、在线预览 |
| 文档管理 | documents | 老版大模型拆段文档管理：上传文档、查看/删除文档、元数据管理 |
| 检索测试 | search | 保留现有功能 |

## 数据模型

### document_files — 文件基础信息

| 字段 | 类型 | 说明 |
|------|------|------|
| id | uuid | 主键 |
| file_name | varchar(500) | 原始文件名 |
| file_path | varchar(500) | S3 源文件路径 |
| content_type | varchar(100) | MIME 类型 |
| created_by | uuid | 上传人 |
| created_at | timestamptz | 创建时间（数据库自动维护） |
| updated_at | timestamptz | 更新时间（数据库自动维护） |

### document_parses — 解析记录

| 字段 | 类型 | 说明 |
|------|------|------|
| id | uuid | 主键 |
| document_file_id | uuid | FK → document_files（级联删除） |
| status | varchar(30) | pending / parsing / parsed / failed |
| external_task_id | varchar(100) | MinerU 任务 ID |
| markdown_content | text | 解析后的 MD（图片路径为 S3 路径） |
| error_message | text | 失败原因 |
| parsed_at | timestamptz | 解析完成时间 |

### document_parse_images — 解析产出的图片

| 字段 | 类型 | 说明 |
|------|------|------|
| id | uuid | 主键 |
| parse_id | uuid | FK → document_parses（级联删除） |
| image_name | varchar(200) | 图片文件名 |
| image_path | varchar(500) | S3 图片路径 |
| content_type | varchar(50) | MIME 类型 |

## API 规格

### API-1：上传文件

`POST /admin/document-files/upload` (multipart/form-data)

- 请求字段：`file`（必填，PDF/DOCX/PPTX，≤200MB）
- 响应：`{ success, data: { id, fileName, contentType } }`
- 错误码：`DOCLIBRARY_FILE_REQUIRED`、`DOCLIBRARY_FILE_FORMAT_UNSUPPORTED`

### API-2：文件列表

`GET /admin/document-files?page=1&pageSize=20&parseStatus=`

- 响应：`{ success, data: [...], total, page, pageSize, totalPages }`
- 每项包含：`id, fileName, contentType, createdAt, createdBy, parseStatus, parsedAt`
- `parseStatus` 取自该文件最新 parse 记录的 status，无 parse 记录时为 null
- 查询参数 `parseStatus`（可选）：
  - 不传或空：返回所有文件
  - `unparsed`：返回 parseStatus 为 null 的文件（未解析）
  - `pending` / `parsing` / `parsed` / `failed`：返回对应状态的文件

### API-3：触发解析

`POST /admin/document-files/{id}/parse`

- 前置条件：文件存在，且无进行中的解析（最新 parse status 不为 pending/parsing）
- 响应：`{ success, data: { id, parseId, status } }`
- 错误码：`DOCLIBRARY_FILE_NOT_FOUND`、`DOCLIBRARY_PARSE_IN_PROGRESS`、`DOCLIBRARY_MINERU_NOT_CONFIGURED`

### API-4：获取文件详情（含 MD）

`GET /admin/document-files/{id}`

- 响应：`{ success, data: { id, fileName, contentType, createdAt, parse: { id, status, markdownContent, errorMessage, parsedAt, images: [...] } } }`
- `markdownContent` 中的图片路径已替换为 S3 presigned URL
- `images` 数组包含每张图片的 `id, imageName, imageUrl`（presigned URL）
- 无解析记录时 `parse` 为 null

### API-5：删除文件

`DELETE /admin/document-files/{id}`

- 同时删除 S3 上的源文件和所有关联解析的图片
- 级联删除 document_parses 和 document_parse_images 记录
- 响应：`{ success, data: { id, deleted } }`

### API-6：按文件 ID 导出为 MD+图片 ZIP

`GET /admin/document-files/{id}/export/markdown`

- 前置条件：文件存在且最新 parse status=parsed
- 响应：`application/zip` 二进制流，文件名 `{fileName}_markdown.zip`
- ZIP 内包含：
  - `{fileName}.md` — Markdown 文件，图片引用为相对路径 `images/{imageName}`
  - `images/` 目录 — 所有引用的图片文件
- 错误码：`DOCLIBRARY_FILE_NOT_FOUND`、`DOCLIBRARY_FILE_NOT_PARSED`

### API-7：按文件 ID 导出为 HTML

`GET /admin/document-files/{id}/export/html`

- 前置条件：文件存在且最新 parse status=parsed
- 响应：`text/html` 二进制流，文件名 `{fileName}.html`
- HTML 为自包含文件：图片以 base64 data URI 内嵌，CSS 内联
- 错误码：`DOCLIBRARY_FILE_NOT_FOUND`、`DOCLIBRARY_FILE_NOT_PARSED`

### API-8：解析记录列表

`GET /admin/document-parses?page=1&pageSize=20&search=`

- 响应：`{ success, data: [...], total, page, pageSize, totalPages }`
- 每项包含：`id, fileName, status, parsedAt, errorMessage`
- `fileName` 来自关联的 document_files.file_name
- 查询参数 `search`（可选）：按文档名模糊匹配
- 按 parsed_at DESC 排序（最新解析在前）

### API-9：删除解析记录

`DELETE /admin/document-parses/{parseId}`

- 仅删除指定的解析记录和关联的 S3 图片
- **不删除**原始文件（document_files 记录保留）
- 级联删除 document_parse_images 记录
- 响应：`{ success, data: { id, deleted } }`
- 错误码：`DOCLIBRARY_PARSE_NOT_FOUND`

### API-10：按解析 ID 导出为 MD+图片 ZIP

`GET /admin/document-parses/{parseId}/export/markdown`

- 前置条件：解析记录存在且 status=parsed
- 响应：`application/zip` 二进制流，文件名 `{fileName}_markdown.zip`
- ZIP 内包含：
  - `{fileName}.md` — Markdown 文件，图片引用为相对路径 `images/{imageName}`
  - `images/` 目录 — 所有引用的图片文件
- 错误码：`DOCLIBRARY_PARSE_NOT_FOUND`、`DOCLIBRARY_PARSE_NOT_PARSED`

### API-11：按解析 ID 导出为 HTML

`GET /admin/document-parses/{parseId}/export/html`

- 前置条件：解析记录存在且 status=parsed
- 响应：`text/html` 二进制流，文件名 `{fileName}.html`
- HTML 为自包含文件：图片以 base64 data URI 内嵌，CSS 内联
- 错误码：`DOCLIBRARY_PARSE_NOT_FOUND`、`DOCLIBRARY_PARSE_NOT_PARSED`

## 详细验收标准

### AC-UPLOAD-01：正常上传

- **Given** 合法 PDF 文件（≤200MB）
- **When** 调用 `POST /admin/document-files/upload`
- **Then** 返回 200，S3 中存在文件，document_files 有记录，无 parse 记录

### AC-UPLOAD-02：文件校验

- **Given** 文件为空或格式不支持
- **When** 调用上传 API
- **Then** 返回 400，对应错误码

### AC-UPLOAD-03：文件超限

- **Given** 文件 > 200MB
- **When** 调用上传 API
- **Then** 返回 400

### AC-PARSE-01：正常解析

- **Given** 文件存在，无进行中的解析，MinerU Token 已配置
- **When** 调用 `POST /admin/document-files/{id}/parse`
- **Then** 新建 document_parses 记录，status=pending，Worker 接管后续流程

### AC-PARSE-02：解析完成

- **Given** MinerU 解析成功
- **When** Worker 完成 ZIP 下载、图片上传、MD 替换
- **Then** parse status=parsed，markdown_content 非空，document_parse_images 有记录

### AC-PARSE-03：解析失败

- **Given** MinerU 解析失败
- **When** Worker 捕获错误
- **Then** parse status=failed，error_message 非空

### AC-PARSE-04：重复解析

- **Given** 文件最新 parse status=failed 或 parsed
- **When** 调用解析 API
- **Then** 新建 parse 记录，status=pending（保留历史记录）

### AC-PARSE-05：解析进行中

- **Given** 文件最新 parse status=pending 或 parsing
- **When** 调用解析 API
- **Then** 返回 422，`DOCLIBRARY_PARSE_IN_PROGRESS`

### AC-PARSE-06：Token 未配置

- **Given** MinerU:ApiToken 为空
- **When** 调用解析 API
- **Then** 返回 503，`DOCLIBRARY_MINERU_NOT_CONFIGURED`

### AC-VIEW-01：查看已解析文件

- **Given** 文件存在，有 parsed 的 parse 记录
- **When** 调用 `GET /admin/document-files/{id}`
- **Then** 返回 markdown_content，图片路径为 presigned URL

### AC-VIEW-02：查看未解析文件

- **Given** 文件存在，无 parse 记录
- **When** 调用 `GET /admin/document-files/{id}`
- **Then** parse 为 null

### AC-DELETE-01：删除文件

- **Given** 文件存在
- **When** 调用 `DELETE /admin/document-files/{id}`
- **Then** 数据库记录级联删除，S3 源文件和关联图片删除

### AC-DELETE-02：删除解析记录

- **Given** 解析记录存在
- **When** 调用 `DELETE /admin/document-parses/{parseId}`
- **Then** 删除解析记录和关联图片（S3 + DB），原始文件保留

### AC-DELETE-03：删除解析记录不影响文件

- **Given** 文件有 2 条解析记录
- **When** 删除其中 1 条
- **Then** 文件和另 1 条解析记录保留

### AC-CONCURRENT-01：并发解析

- **Given** 多个文件同时触发解析
- **When** Worker 处理
- **Then** 每个文件独立处理，互不影响

### AC-EXPORT-01：按文件 ID 导出 MD+图片 ZIP

- **Given** 文件最新 parse status=parsed
- **When** 调用 `GET /admin/document-files/{id}/export/markdown`
- **Then** 返回 ZIP 文件，包含 `.md` 文件和 `images/` 目录

### AC-EXPORT-02：按文件 ID 导出 HTML

- **Given** 文件最新 parse status=parsed
- **When** 调用 `GET /admin/document-files/{id}/export/html`
- **Then** 返回自包含 HTML 文件，图片以 base64 data URI 内嵌

### AC-EXPORT-03：导出未解析文件

- **Given** 文件无 parsed 的 parse 记录
- **When** 调用按文件 ID 的导出 API
- **Then** 返回 422，`DOCLIBRARY_FILE_NOT_PARSED`

### AC-EXPORT-04：导出不存在的文件

- **Given** 文件 ID 不存在
- **When** 调用按文件 ID 的导出 API
- **Then** 返回 404，`DOCLIBRARY_FILE_NOT_FOUND`

### AC-EXPORT-05：按解析 ID 导出 MD+图片 ZIP

- **Given** 解析记录存在且 status=parsed
- **When** 调用 `GET /admin/document-parses/{parseId}/export/markdown`
- **Then** 返回 ZIP 文件，包含 `.md` 文件和 `images/` 目录

### AC-EXPORT-06：按解析 ID 导出 HTML

- **Given** 解析记录存在且 status=parsed
- **When** 调用 `GET /admin/document-parses/{parseId}/export/html`
- **Then** 返回自包含 HTML 文件，图片以 base64 data URI 内嵌

### AC-EXPORT-07：按解析 ID 导出未解析记录

- **Given** 解析记录 status 不为 parsed
- **When** 调用按解析 ID 的导出 API
- **Then** 返回 422，`DOCLIBRARY_PARSE_NOT_PARSED`

### AC-PREVIEW-01：在线预览解析记录

- **Given** 解析记录存在且 status=parsed
- **When** 点击"预览"按钮
- **Then** 在新浏览器 tab 中打开自包含 HTML 页面，展示解析后的文档内容

### AC-PREVIEW-02：预览未解析记录

- **Given** 解析记录 status 不为 parsed
- **When** 点击"预览"按钮
- **Then** 按钮不可用（disabled）

### AC-LIST-01：文件列表过滤

- **Given** 系统有 5 个文件，3 个未解析，2 个已解析
- **When** 调用 `GET /admin/document-files?parseStatus=unparsed`
- **Then** 返回 3 个未解析文件

### AC-LIST-02：解析记录列表

- **Given** 系统有 3 条解析记录
- **When** 调用 `GET /admin/document-parses`
- **Then** 返回 3 条记录，每条包含 fileName, status, parsedAt

### AC-LIST-03：解析记录搜索

- **Given** 有解析记录关联文件名为 "英语三年级.pdf" 和 "数学五年级.pdf"
- **When** 调用 `GET /admin/document-parses?search=英语`
- **Then** 仅返回 "英语三年级.pdf" 的解析记录

### AC-SPLIT-01：大文档自动拆分解析

- **Given** 上传的 PDF 超过 200 页
- **When** Worker 处理该文件的解析任务
- **Then** 自动将 PDF 按每 200 页拆分为多个子文档，分别提交 MinerU 解析

### AC-SPLIT-02：拆分后合并结果

- **Given** 大文档被拆分为 N 个子文档，全部解析完成
- **When** Worker 合并结果
- **Then** 所有子文档的 Markdown 按顺序拼接为一份完整 Markdown，图片统一管理，存为一条 parse 记录

### AC-SPLIT-03：拆分后部分失败

- **Given** 大文档被拆分为 3 个子文档，其中 1 个解析失败
- **When** Worker 检测到失败
- **Then** 整个 parse 标记为 failed，error_message 包含失败的子任务信息；已成功的子任务图片保留在 S3

### AC-SPLIT-04：200 页以内的文档

- **Given** 上传的 PDF 为 150 页
- **When** Worker 处理该文件的解析任务
- **Then** 不拆分，直接提交 MinerU 解析（与现有流程一致）

### AC-CONVERT-01：非 PDF 文件自动转 PDF

- **Given** 上传的文件为 DOCX/PPTX 格式
- **When** Worker 处理该文件的解析任务
- **Then** 先通过 LibreOffice headless 将文件转换为 PDF，再按 PDF 流程处理（检测页数→拆分→提交解析）

### AC-CONVERT-02：PDF 文件不转换

- **Given** 上传的文件为 PDF 格式
- **When** Worker 处理该文件的解析任务
- **Then** 直接进入页数检测流程，不做格式转换

### AC-CONVERT-03：转换失败

- **Given** 上传的 DOCX 文件损坏或格式不支持
- **When** LibreOffice 转换失败
- **Then** parse 标记为 failed，error_message 包含转换失败原因

### AC-CONVERT-04：LibreOffice 未安装

- **Given** 服务器未安装 LibreOffice
- **When** Worker 尝试转换非 PDF 文件
- **Then** parse 标记为 failed，error_message 提示 LibreOffice 未配置

## 解析状态流转

```
触发解析 ──▶ pending ──Worker开始──▶ parsing ──成功──▶ parsed
                                      │
                                      │ 失败
                                      ▼
                                    failed (可重试，新建记录)
```

## 非功能需求

- **刷新安全**：所有状态在数据库，前端刷新不影响
- **并发安全**：多个文件可同时解析
- **文件与解析解耦**：文件可独立存在，解析为可选操作；未来可支持其他解析方式
- **图片管理**：图片上传到 S3 的 `documents/mineru/{taskId}/{imageName}` 路径，MD 中存储 S3 路径，查看时替换为 presigned URL
- **Presigned URL 有效期**：1 小时
