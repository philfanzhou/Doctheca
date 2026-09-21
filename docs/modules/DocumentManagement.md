# DocumentManagement — 文档文件管理

> 原 DocumentManagement 六件套收敛后的单一能力文档。存储主责按 [ADR-0009](../../../../../docs/adr/0009-doclibrary-structadoc-parse-migration.md) 迁移：新文档原件由 StructaDoc 主责，本地只存引用与元数据。

## 能力概述

文档文件全生命周期管理：上传（转发 StructaDoc）、分页列表、详情查看、元数据更新、删除（联动清理本地数据、存量 OSS 对象与 StructaDoc 文档）、触发解析。本地 `document_files` 表保存文件名、MIME、学科/年级/年份与 `structadoc_document_id` 引用；存量记录保留 `file_path`（OSS 路径）。

## 端点与契约

### POST /admin/document-files/upload

- MIME 白名单：PDF、DOC、DOCX、PPT、PPTX；大小上限 200MB（本服务侧校验）。
- 文件字节直接转发 StructaDoc `POST /api/v1/documents`（不再写入 OSS）；本地记录 `structadoc_document_id`，`content_type` 采用 StructaDoc 内容嗅探结果。
- `created_by` 取管理员 JWT `sub`；缺失或非 UUID 时保持 null，不影响上传。
- 错误码：

| 场景 | 状态码 | errorCode |
|------|--------|-----------|
| StructaDoc 未配置 | 503 | `DOCLIBRARY_STRUCTADOC_NOT_CONFIGURED` |
| 空文件 | 400 | `DOCLIBRARY_FILE_REQUIRED` |
| 格式不支持（本地白名单或 StructaDoc 415） | 400 | `DOCLIBRARY_FILE_FORMAT_UNSUPPORTED` |
| 超过本地 200MB / StructaDoc 上限（413） | 400 | — |
| StructaDoc 拒绝 API key（401/403） | 502 | `DOCLIBRARY_STRUCTADOC_UNAUTHORIZED` |
| 其他 StructaDoc 失败 | 502 | `DOCLIBRARY_STRUCTADOC_ERROR` |

### GET /admin/document-files

分页 `page`/`pageSize`；`fileName` 模糊搜索；`parseStatus` 过滤（`unparsed` 或 `pending`/`parsing`/`parsed`/`failed`，按最新 parse 内存过滤）；返回 total/page/pageSize/totalPages。

### GET /admin/document-files/{id}

- 返回文件信息 + 全部 parses（状态、markdown、contentList 系列、errorMessage、parsedAt、images）。
- 图片 URL 双路径：**新解析**（有 `structadoc_parse_run_id`）→ 代理端点 `/admin/document-parses/{parseId}/images/{imageId}/content`；**存量解析** → OSS presigned URL（3600s，失败回退原始路径）。
- markdown 内图片引用按同一来源替换。

### PUT /admin/document-files/{id}/metadata

更新 `subject`/`grade`/`year`（null 字段不修改）；best-effort 同步 OpenSearch（update_by_query），失败仅记 Warning。

### DELETE /admin/document-files/{id}

1. 收集**存量** OSS 路径（`file_path` + 存量 parse 的 `zip_path` 与图片路径；新解析的 image_path 是 StructaDoc asset id，不参与 OSS 清理）。
2. 删除本地数据库记录（级联 parses → blocks/images）。
3. Best-effort 清理 OSS 对象（单条失败不阻塞，返回 `ossDeleted`/`ossFailed`）。
4. 有 `structadoc_document_id` 时：先 best-effort cancel 未终态 Parse Run，再 `DELETE /api/v1/documents/{id}`（202 受理即视为删除；失败记 Warning，返回 `structaDocDeleted`）。
5. Best-effort 删除 OpenSearch 索引。

### POST /admin/document-files/{id}/parse

校验序列：modelVersion ∈ {`vlm`, `pipeline`} → 文件存在 → 同 modelVersion 无进行中解析（422 `DOCLIBRARY_PARSE_IN_PROGRESS`）→ StructaDoc 已配置（503 `DOCLIBRARY_STRUCTADOC_NOT_CONFIGURED`）。通过后创建 `status=pending` 的 parse 记录；实际提交与轮询见 [DocumentParse](./DocumentParse.md)。

## 数据

`document_files` 关键列：`file_path`（可空，仅存量）、`structadoc_document_id`（uuid 可空，新文档必填其一）、`content_type`、`created_by`、`subject`/`grade`/`year`、`created_at`/`updated_at`（触发器维护）。详见 [database/tables/document_files.md](../database/tables/document_files.md)。

## 验证

- 单元测试：`DocumentFileServiceTests/`（含 Attach 引用回填）、`Authentication/DocumentFileUploadAuthorizationTests`（createdBy、未配置 503）、`Authentication/AuthorizationBoundaryTests`（401/403 边界）、`DocumentFileDeleteCleanupTests`（存量路径收集）。
- 命令：`dotnet test src/Ruoyu.Study.DocLibrary.sln --configuration Release`（在本服务目录）。
