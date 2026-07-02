# Integration — 集成矩阵

## 集成总表

| 目标 | 协议 | 方向 | 用途 | 降级策略 |
|------|------|------|------|---------|
| **Admin UI / HTTP 客户端** | HTTP | 入 | 文档管理 API + 搜索 | 无降级（用户直接操作） |
| **PostgreSQL** | TCP | 出 | 文档 CRUD + 倒排索引搜索 | 无可降级，返回 500 错误 |
| **MinIO/SeaweedFS** | S3 | 出 | 文件上传下载 | 上游返回错误，上传/解析失败 |
| **OpenSearch** | HTTP | 出 | 全文搜索索引写/查 | 索引失败不阻塞主流程，搜索回退到数据库 |
| **QuantumZhou.Identity** | gRPC | 出 | JWT 签发（GetToken） | 登录/刷新失败返回 401/503 |

## HTTP API（入方向）

### 认证端点（AllowAnonymous）

定义在 Identity.Client SDK（[AuthEndpoints.cs](../../../../QuantumZhou.Identity/backend/Client/AuthEndpoints.cs)）：

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/admin/auth/login` | 用户名密码登录，返回 JWT + RefreshToken |
| POST | `/admin/auth/refresh` | 使用 RefreshToken 刷新 JWT |

### 认证端点（需认证）

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/admin/auth/me` | 获取当前用户信息 |
| POST | `/admin/auth/logout` | 登出（吊销 RefreshToken） |

### 文档管理端点（需认证）

定义在 [DocumentAdminEndpoints.cs](../../src/Service/DocumentAdminEndpoints.cs)：

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/admin/documents/upload` | 上传文档文件（PDF/DOC/DOCX/PPT/PPTX，最大 200MB） |
| GET | `/admin/documents` | 分页列出文档 |
| GET | `/admin/documents/{id}` | 获取文档详情 |
| GET | `/admin/documents/{id}/status` | 查询文档解析状态 |
| DELETE | `/admin/documents/{id}` | 按 ID 删除文档 |
| DELETE | `/admin/documents/by-title/{title}` | 按标题删除文档 |
| PUT | `/admin/documents/{title}/metadata` | 更新文档元数据 |
| GET | `/admin/documents/search` | 精确关键词搜索（支持 subject/grade/year/documentTitle 过滤） |
| GET | `/health` | 健康检查 |

## 失败语义

### OpenSearch 写失败
- 索引文档时失败：Worker 记录 error 日志，**不标记 job 失败**，文档仍然搜索可用（通过 DB 倒排索引）
- 搜索时无法连接：`SearchDomainService` 自动回退到 PostgreSQL 倒排索引搜索

### OSS 下载失败
- 导入 Worker 下载文件失败：job 标记为 `failed`，记录 `error_message`