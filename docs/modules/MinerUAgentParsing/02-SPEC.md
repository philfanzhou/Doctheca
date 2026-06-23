# MinerU 文档解析（持久化版）— 详细需求规格 (SPEC)

## 功能概述

本模块实现三阶段文档管理流程：文件上传 → MinerU 解析 → Markdown 查看。所有状态持久化在数据库中，前端无状态，刷新安全。

## API 规格

### API-1：上传文件

`POST /admin/document-files/upload` (multipart/form-data)

- 请求字段：`file`（必填，PDF/DOCX/PPTX，≤200MB）
- 响应：`{ success, data: { id, fileName, fileSize, status } }`
- 错误码：`DOCRETRIEVAL_FILE_REQUIRED`、`DOCRETRIEVAL_FILE_FORMAT_UNSUPPORTED`

### API-2：文件列表

`GET /admin/document-files?page=1&pageSize=20&status=`

- 响应：`{ success, data: [...], total, page, pageSize, totalPages }`
- 每项包含：`id, fileName, fileSize, contentType, status, errorMessage, createdAt, createdBy, parsedAt`

### API-3：触发解析

`POST /admin/document-files/{id}/parse`

- 前置条件：`status == uploaded || status == parse_failed`
- 响应：`{ success, data: { id, status } }`
- 错误码：`DOCRETRIEVAL_FILE_NOT_FOUND`、`DOCRETRIEVAL_FILE_NOT_PARSEABLE`、`DOCRETRIEVAL_MINERU_NOT_CONFIGURED`

### API-4：获取文件详情（含 MD）

`GET /admin/document-files/{id}`

- 响应：`{ success, data: { id, fileName, fileSize, contentType, status, markdownContent, errorMessage, images: [...], createdAt, parsedAt } }`
- `markdownContent` 中的图片路径已替换为 S3 presigned URL
- `images` 数组包含每张图片的 `id, imageName, imageUrl`（presigned URL）

### API-5：删除文件

`DELETE /admin/document-files/{id}`

- 同时删除 S3 上的源文件和关联图片
- 响应：`{ success, data: { id, deleted } }`

## 详细验收标准

### AC-UPLOAD-01：正常上传

- **Given** 合法 PDF 文件（≤200MB）
- **When** 调用 `POST /admin/document-files/upload`
- **Then** 返回 200，`status=="uploaded"`，S3 中存在文件，数据库存在记录

### AC-UPLOAD-02：文件校验

- **Given** 文件为空或格式不支持
- **When** 调用上传 API
- **Then** 返回 400，对应错误码

### AC-UPLOAD-03：文件超限

- **Given** 文件 > 200MB
- **When** 调用上传 API
- **Then** 返回 400

### AC-PARSE-01：正常解析

- **Given** 文件 status=uploaded，MinerU Token 已配置
- **When** 调用 `POST /admin/document-files/{id}/parse`
- **Then** status 变为 `pending_parse`，IngestionWorker 接管后续流程

### AC-PARSE-02：解析完成

- **Given** MinerU 解析成功
- **When** Worker 完成 ZIP 下载、图片上传、MD 替换
- **Then** status=`parsed`，`markdown_content` 非空，`document_file_images` 有记录

### AC-PARSE-03：解析失败

- **Given** MinerU 解析失败
- **When** Worker 捕获错误
- **Then** status=`parse_failed`，`error_message` 非空

### AC-PARSE-04：重复解析

- **Given** 文件 status=parse_failed
- **When** 调用解析 API
- **Then** 允许重试，status 变为 pending_parse

### AC-PARSE-05：不可解析状态

- **Given** 文件 status=parsing 或 parsed
- **When** 调用解析 API
- **Then** 返回 422，`DOCRETRIEVAL_FILE_NOT_PARSEABLE`

### AC-PARSE-06：Token 未配置

- **Given** MinerU:ApiToken 为空
- **When** 调用解析 API
- **Then** 返回 503，`DOCRETRIEVAL_MINERU_NOT_CONFIGURED`

### AC-VIEW-01：查看已解析文件

- **Given** 文件 status=parsed
- **When** 调用 `GET /admin/document-files/{id}`
- **Then** 返回 markdown_content，图片路径为 presigned URL

### AC-VIEW-02：查看未解析文件

- **Given** 文件 status=uploaded
- **When** 调用 `GET /admin/document-files/{id}`
- **Then** markdown_content 为 null，images 为空数组

### AC-DELETE-01：删除文件

- **Given** 文件存在
- **When** 调用 `DELETE /admin/document-files/{id}`
- **Then** 数据库记录删除，S3 源文件和关联图片删除

### AC-CONCURRENT-01：并发解析

- **Given** 多个文件同时触发解析
- **When** IngestionWorker 处理
- **Then** 每个文件独立处理，互不影响

## 状态流转

```
uploaded ──触发解析──▶ pending_parse ──Worker开始──▶ parsing ──成功──▶ parsed
   │                                                    │
   │                                                    │ 失败
   │                                                    ▼
   └────────────────────────────────────────────── parse_failed
                                                      (可重试)
```

## 非功能需求

- **刷新安全**：所有状态在数据库，前端刷新不影响
- **并发安全**：多个文件可同时解析
- **图片管理**：图片上传到 S3 的 `documents/mineru/{taskId}/{imageName}` 路径，MD 中存储 S3 路径，查看时替换为 presigned URL
- **Presigned URL 有效期**：1 小时
