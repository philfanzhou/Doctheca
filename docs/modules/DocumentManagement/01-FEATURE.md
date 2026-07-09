# 01-FEATURE — DocumentManagement 文档文件管理

## 功能概述

提供文档文件的全生命周期管理：上传、分页列表、详情查看、元数据更新、删除（含联动清理），以及触发 MinerU 解析。文件本体存储在 OSS（S3/MinIO），元数据（文件名、路径、MIME 类型、学科/年级/年份）存储在 PostgreSQL `document_files` 表。

## 背景

DocLibrary 是内网管理后台，文档文件是 MinerU 解析流程的入口。上传后的文件由 `MinerUFileParseWorker` 后台 Worker 拉取并提交到 MinerU Precision API 解析。本模块负责文件上传后的元数据管理与清理，不涉及解析过程本身（解析详见 DocumentParse 模块）。

## 用户故事

- **作为老师**:我上传一份 PDF 讲义，系统保存文件并返回文件 ID
- **作为老师**:我按学科/年级/解析状态浏览已上传的文档列表
- **作为老师**:我查看某文档的详情，包括所有解析记录和图片预览
- **作为老师**:我手动设置文档的学科/年级/年份元数据，用于搜索过滤
- **作为老师**:我删除某文档，系统自动清理关联的解析记录、图片和 OSS 文件
- **作为运维**:我触发某文档重新解析，系统创建一条待处理的解析记录

## 功能需求

### FR-01:上传文档文件
- `POST /admin/document-files/upload`
- 支持格式:PDF (`application/pdf`)、DOC (`application/msword`)、DOCX (`application/vnd.openxmlformats-officedocument.wordprocessingml.document`)、PPT (`application/vnd.ms-powerpoint`)、PPTX (`application/vnd.openxmlformats-officedocument.presentationml.presentation`)
- 最大文件大小:200MB
- 文件上传至 OSS bucket `Documents`，object name 为 `{Guid}{ext}`
- 返回文件 ID、文件名、MIME 类型
- 上传时 `created_by` 为 null（内网管理后台无应用层认证）

### FR-02:分页列出文档文件
- `GET /admin/document-files`
- 支持分页参数 `page`(默认 1)、`pageSize`(默认 20)
- 支持按文件名模糊搜索 `fileName`
- 支持按解析状态过滤 `parseStatus`（`unparsed` 表示未解析，或具体状态值如 `pending`/`parsing`/`parsed`/`failed`）
- 返回分页元数据（total、page、pageSize、totalPages）

### FR-03:获取文档文件详情
- `GET /admin/document-files/{id}`
- 返回文件基本信息（ID、文件名、MIME 类型、创建时间）
- 返回所有关联的解析记录（parses），每条包含解析状态、Markdown 内容、图片列表（含 presigned URL，有效期 3600 秒）
- Markdown 内容中的图片路径替换为 presigned URL

### FR-04:更新文档文件元数据
- `PUT /admin/document-files/{id}/metadata`
- 请求体:`{ "subject": "...", "grade": "...", "year": "..." }`（全部可选，null 表示不修改）
- 更新后 best-effort 同步 OpenSearch 索引（按 `document_file_id` 更新所有 blocks 的 subject/grade/year）
- OpenSearch 同步失败仅记 Warning 日志，不阻塞

### FR-05:删除文档文件及联动清理
- `DELETE /admin/document-files/{id}`
- 联动清理顺序:
  1. 收集所有 OSS 路径（源文件 + 解析 zip + 解析图片）
  2. 删除数据库记录（级联删除解析记录和图片记录）
  3. Best-effort 删除 OSS 文件（单条失败不阻塞）
  4. Best-effort 删除 OpenSearch 索引（按 `document_file_id` 删除，不阻塞）
- 返回删除结果（ossDeleted / ossFailed 计数）

### FR-06:触发文档解析
- `POST /admin/document-files/{id}/parse`
- 查询参数 `modelVersion`（默认 `vlm`，可选 `pipeline`）
- 校验文件存在、modelVersion 合法、同 modelVersion 无进行中的解析（pending/parsing）、MinerU ApiToken 已配置
- 创建 `document_parses` 记录，`status` 为 `pending`
- 返回 parseId 和初始状态

## 验收条件

| AC | 描述 |
|----|------|
| AC-01 | 上传 PDF/DOC/DOCX/PPT/PPTX 文件成功，返回 id/fileName/contentType |
| AC-02 | 上传不支持的格式返回 400 + `DOCLIBRARY_FILE_FORMAT_UNSUPPORTED` |
| AC-03 | 上传空文件返回 400 + `DOCLIBRARY_FILE_REQUIRED` |
| AC-04 | 上传超过 200MB 文件返回 400 |
| AC-05 | 列表支持分页、fileName 模糊搜索、parseStatus 过滤 |
| AC-06 | 详情返回文件信息 + parses（含图片 presigned URL） |
| AC-07 | 触发解析创建 status=`pending` 的 parse 记录 |
| AC-08 | 同 modelVersion 有进行中解析时返回 422 + `DOCLIBRARY_PARSE_IN_PROGRESS` |
| AC-09 | MinerU ApiToken 未配置时返回 503 + `DOCLIBRARY_MINERU_NOT_CONFIGURED` |
| AC-10 | 更新元数据后 OpenSearch 索引同步刷新（best-effort） |
| AC-11 | 删除文件时级联删除解析记录、图片记录、OSS 文件、OpenSearch 索引 |
| AC-12 | 不存在的文件 ID 返回 404 + `DOCLIBRARY_FILE_NOT_FOUND` |

## 非功能需求

| NFR | 描述 |
|-----|------|
| NFR-01 | 文件本体存储于 OSS（S3/MinIO），数据库仅存元数据与路径 |
| NFR-02 | 删除联动 OSS/OpenSearch 为 best-effort，单条失败不阻塞整体删除 |
| NFR-03 | 列表的 parseStatus 过滤在内存中完成（先拉取数据再过滤），非 DB 查询 |
| NFR-04 | 无应用层认证（内网管理后台，访问控制由部署层网络隔离实现） |
| NFR-05 | 图片 presigned URL 有效期 3600 秒 |
| NFR-06 | 上传使用 `multipart/form-data`，form file 字段名为 `file` |

## 数据来源

- **文件元数据**:`document_files` 表
- **解析记录**:`document_parses` 表（关联 `document_file_id`）
- **解析图片**:`document_parse_images` 表（关联 `parse_id`）
- **文件本体**:OSS bucket `Documents`，路径前缀 `doclibrary-files/`
- **MinerU 配置**:`MinerU` 配置段（`ApiToken`/`BaseUrl`/`ModelVersion`）

## 接口清单

| 组件 | 修改 |
|------|------|
| `DocumentFileEntity` | 文件实体（id/file_name/file_path/content_type/created_by/subject/grade/year/created_at/updated_at） |
| `DocumentFileModel` | 文件领域模型 |
| `IDocumentFileRepository` | 文件仓储接口（AddAsync/GetByIdAsync/GetListAsync/UpdateAsync/UpdateMetadataAsync/DeleteAsync） |
| `IDocumentFileService` | 文件领域服务接口（CreateAsync/GetByIdAsync/GetListAsync/UpdateMetadataAsync/DeleteAsync） |
| `DocumentFileRepository` | 文件仓储实现 |
| `DocumentFileService` | 文件领域服务实现 |
| `DocumentFileEndpoints` | HTTP 端点映射（`/admin/document-files`） |
| `UpdateMetadataRequest` | 元数据更新请求 DTO（record） |
| `ISearchIndexService` | OpenSearch 索引服务（`UpdateDocumentFileMetadataAsync`/`DeleteDocumentFileIndexAsync`） |
