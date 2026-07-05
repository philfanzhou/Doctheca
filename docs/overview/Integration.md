# Integration — 集成矩阵

## 集成总表

| 目标 | 协议 | 方向 | 用途 | 降级策略 |
|------|------|------|------|---------|
| **Admin UI / HTTP 客户端** | HTTP | 入 | 文档管理 API + 搜索 | 无降级（用户直接操作） |
| **QuestionBank 服务** | HTTP | 入 | 拉模式获取 MinerU 解析数据 + 回写导入状态 | 调用方自行重试，DocLibrary 仅作为数据源 |
| **PostgreSQL** | TCP | 出 | 文档/解析数据 CRUD | 无可降级，返回 500 错误 |
| **MinIO/SeaweedFS** | S3 | 出 | 文件上传下载 | 上游返回错误，上传/解析失败 |
| **OpenSearch** | HTTP | 出 | 全文搜索索引写/查(MinerU blocks 索引源) | 索引失败不阻塞主流程，搜索不可用时返回空结果 |
| **LLM (OpenAI 兼容)** | HTTP | 出 | 文档解析完成后分析 subject/grade/year 元数据(best-effort) | LLM 未配置或调用失败时跳过,不影响解析与索引主流程 |

> **访问控制**：DocLibrary 为内网管理后台，所有 `/admin/*` 端点 `AllowAnonymous`，不调用任何认证服务。访问控制由部署层网络隔离实现（仅内网可访问 `:5012` 端口）。

## HTTP API（入方向）

### 文档管理端点

定义在 [DocumentFileEndpoints.cs](../../src/Service/Endpoints/DocumentFileEndpoints.cs) / [DocumentParseEndpoints.cs](../../src/Service/Endpoints/DocumentParseEndpoints.cs) / [DocumentSearchEndpoints.cs](../../src/Service/Endpoints/DocumentSearchEndpoints.cs) / [DocumentExportEndpoints.cs](../../src/Service/Endpoints/DocumentExportEndpoints.cs)：

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/admin/document-files/upload` | 上传文档文件（PDF/DOC/DOCX/PPT/PPTX，最大 200MB） |
| GET | `/admin/document-files` | 分页列出文档文件 |
| GET | `/admin/document-files/{id}` | 获取文档文件详情 |
| DELETE | `/admin/document-files/{id}` | 按 ID 删除文档文件（联动删除解析、图片、OpenSearch 索引） |
| PUT | `/admin/document-files/{id}/metadata` | 更新 document_files 元数据（subject/grade/year），并同步 OpenSearch 索引 |
| POST | `/admin/document-files/{id}/parse` | 触发 MinerU 解析 |
| GET | `/admin/document-parses/{parseId}` | 查询解析状态与结果 |
| DELETE | `/admin/document-parses/{parseId}` | 删除解析记录（联动删除 blocks、images、OpenSearch 索引） |
| GET | `/admin/documents/search` | 精确关键词搜索（支持 subject/grade/year/documentTitle 过滤） |
| GET | `/health` | 健康检查 |

### QuestionBank 拉模式导入端点

定义在 [QuestionBankImportEndpoints.cs](../../src/Service/Endpoints/QuestionBankImportEndpoints.cs)：

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/admin/document-parses/importable` | 分页查询可导入的 parse 列表（status=parsed），支持 search 模糊搜索、includeImported 控制是否包含已导入 |
| GET | `/admin/document-parses/{parseId}/blocks` | 按 parseId 分页获取结构化块，支持 pageId / blockType 过滤，image 类型 block 额外返回 presigned URL |
| GET | `/admin/document-parses/images/{imageId}` | 按 imageId 获取图片二进制流（从 OSS 下载，返回正确 MIME） |
| POST | `/admin/document-parses/{parseId}/import-status` | 回写 parse 的导入状态（首次插入或更新），防重复导入（imported→imported 返回 422） |

**调用方**：QuestionBank 服务（直接 HTTP 调用，无认证）
**数据范围**：只读 MinerU 解析链路（`document_parses` / `document_parse_blocks` / `document_parse_images`）
**详细规格**：[QuestionBankImport 模块文档](../modules/QuestionBankImport/01-FEATURE.md)

## 失败语义

### OpenSearch 写失败
- 索引文档时失败：Worker 记录 error 日志，**不标记解析失败**，解析结果仍持久化在数据库中，仅搜索不可用
- 搜索时无法连接：`SearchDomainService` 返回空结果并记录 LogWarning

### OpenSearch 索引源

详见 [OpenSearchBlockIndexing 模块](../modules/OpenSearchBlockIndexing/01-FEATURE.md):

- **索引源**:`document_parse_blocks` 表(MinerU 解析链路)
- **索引时机**:MinerUFileParseWorker 解析状态变为 `parsed` 后自动索引
- **删除时机**:
  - `DELETE /admin/document-parses/{parseId}` → 按 `parse_id` 删除索引
  - `DELETE /admin/document-files/{id}` → 按 `document_file_id` 删除索引
- **索引字段**:`parse_id` / `document_file_id` / `file_name` / `block_id` / `block_type` / `text` / `page_number` / `sort_index` / `image_id` / `created_at`
- **元数据**:`subject` / `grade` / `year` 来源为 `document_files` 表(2026-07 新增 nullable 列)。可通过 `PUT /admin/document-files/{id}/metadata` 手动设置,或在 MinerU 解析完成后由 LLM 自动填充缺失字段(best-effort,详见 [DocumentMetadataAnalysis 模块](../modules/DocumentMetadataAnalysis/01-FEATURE.md))。OpenSearch 索引中文档级元数据随解析完成或手动更新同步刷新

### OSS 下载失败
- 解析 Worker 下载文件失败：解析记录标记为 `failed`，记录 `error_message`
- QuestionBank 拉模式 `GET /images/{imageId}` 下载失败：返回 500 + `DOCLIBRARY_OSS_DOWNLOAD_FAILED`，调用方自行重试

### QuestionBank 拉模式错误码

| 错误码 | HTTP | 触发场景 |
|--------|------|---------|
| `DOCLIBRARY_PARSE_NOT_FOUND` | 404 | parseId 不存在 |
| `DOCLIBRARY_PARSE_NOT_PARSED` | 422 | parse 状态非 `parsed`（如 `parsing` / `failed`） |
| `DOCLIBRARY_IMAGE_NOT_FOUND` | 404 | imageId 不存在 |
| `DOCLIBRARY_OSS_DOWNLOAD_FAILED` | 500 | OSS 下载图片失败 |
| `DOCLIBRARY_IMPORT_STATUS_INVALID` | 400 | status 非 `imported`/`failed`，或 `importedBy` 缺失/非 UUID |
| `DOCLIBRARY_PARSE_ALREADY_IMPORTED` | 422 | parse 已是 `imported` 状态，再次标记 `imported` 被拒绝 |