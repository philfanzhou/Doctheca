# Integration — 集成矩阵

## 集成总表

| 目标 | 协议 | 方向 | 用途 | 降级策略 |
|------|------|------|------|---------|
| **Admin UI / HTTP 客户端** | HTTP | 入 | 文档管理 API + 搜索 | 无降级（用户直接操作） |
| **QuestionBank 服务** | HTTP | 入 | 拉模式获取 MinerU 解析数据 + 回写导入状态 | 调用方自行重试，DocLibrary 仅作为数据源 |
| **PostgreSQL** | TCP | 出 | 文档 CRUD + 倒排索引搜索 | 无可降级，返回 500 错误 |
| **MinIO/SeaweedFS** | S3 | 出 | 文件上传下载 | 上游返回错误，上传/解析失败 |
| **OpenSearch** | HTTP | 出 | 全文搜索索引写/查 | 索引失败不阻塞主流程，搜索回退到数据库 |
| **QuantumZhou.Identity** | HTTP | 出 | JWT 签发（`POST /api/auth/token`，OAuth2 grant_type 模式）+ JWKS 公钥验证（OIDC discovery） | 登录/刷新失败返回 401/502 |

## HTTP API（入方向）

### 认证端点（AllowAnonymous）

定义在 [AuthEndpoints.cs](../../src/Service/Endpoints/AuthEndpoints.cs)，通过 `IHttpClientFactory` 调用 Identity HTTP API `POST /api/auth/token`：

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/admin/auth/login` | 用户名密码登录（grant_type=password），返回 JWT + RefreshToken |
| POST | `/admin/auth/refresh` | 使用 RefreshToken 刷新 JWT（grant_type=refresh_token） |

### 认证端点（需认证）

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/admin/auth/me` | 获取当前用户信息（从 JWT claims 读取，不调用 Identity） |
| POST | `/admin/auth/logout` | 登出（前端清除 Token，不调用 Identity） |

### 文档管理端点（需认证）

定义在 [DocumentFileEndpoints.cs](../../src/Service/Endpoints/DocumentFileEndpoints.cs) / [DocumentParseEndpoints.cs](../../src/Service/Endpoints/DocumentParseEndpoints.cs) / [DocumentSearchEndpoints.cs](../../src/Service/Endpoints/DocumentSearchEndpoints.cs) / [DocumentExportEndpoints.cs](../../src/Service/Endpoints/DocumentExportEndpoints.cs)：

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/admin/documents/upload` | 上传文档文件（PDF/DOC/DOCX/PPT/PPTX，最大 200MB） |
| GET | `/admin/documents` | 分页列出文档 |
| GET | `/admin/documents/{id}` | 获取文档详情 |
| GET | `/admin/documents/{id}/status` | 查询文档解析状态 |
| DELETE | `/admin/documents/{id}` | 按 ID 删除文档 |
| PUT | `/admin/documents/{title}/metadata` | 更新文档元数据 |
| GET | `/admin/documents/search` | 精确关键词搜索（支持 subject/grade/year/documentTitle 过滤） |
| GET | `/health` | 健康检查 |

### QuestionBank 拉模式导入端点（需认证）

定义在 [QuestionBankImportEndpoints.cs](../../src/Service/Endpoints/QuestionBankImportEndpoints.cs)：

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/admin/document-parses/importable` | 分页查询可导入的 parse 列表（status=parsed），支持 search 模糊搜索、includeImported 控制是否包含已导入 |
| GET | `/admin/document-parses/{parseId}/blocks` | 按 parseId 分页获取结构化块，支持 pageId / blockType 过滤，image 类型 block 额外返回 presigned URL |
| GET | `/admin/document-parses/images/{imageId}` | 按 imageId 获取图片二进制流（从 OSS 下载，返回正确 MIME） |
| POST | `/admin/document-parses/{parseId}/import-status` | 回写 parse 的导入状态（首次插入或更新），防重复导入（imported→imported 返回 422） |

**调用方**：QuestionBank 服务（携带 JWT Bearer Token）
**数据范围**：只读 MinerU 链路（`document_parses` / `document_parse_blocks` / `document_parse_images`），不触碰 IngestionWorker 链路
**详细规格**：[QuestionBankImport 模块文档](../modules/QuestionBankImport/01-FEATURE.md)

## 失败语义

### OpenSearch 写失败
- 索引文档时失败：Worker 记录 error 日志，**不标记 job 失败**，文档仍然搜索可用（通过 DB 倒排索引）
- 搜索时无法连接：`SearchDomainService` 自动回退到 PostgreSQL 倒排索引搜索

### OSS 下载失败
- 导入 Worker 下载文件失败：job 标记为 `failed`，记录 `error_message`
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