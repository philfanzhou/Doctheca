# Integration — 集成矩阵

## 集成总表

| 目标 | 协议 | 方向 | 用途 | 降级策略 |
|------|------|------|------|---------|
| **Admin UI** | HTTP | 入 | 文档管理 API | 无降级（用户直接操作） |
| **其他服务** | gRPC | 入 | ExactSearch, HybridSearch | 无降级（同步返回结果） |
| **PostgreSQL** | TCP | 出 | 文档 CRUD + 倒排索引搜索 | 无可降级，返回 gRPC 错误 |
| **MinIO/SeaweedFS** | S3 | 出 | 文件上传下载 | 上游返回错误，上传/解析失败 |
| **OpenSearch** | HTTP | 出 | 全文搜索索引写/查 | 索引失败不阻塞主流程，搜索回退到数据库 |
| **Qdrant** | HTTP/gRPC | 出 | 向量写入 + 语义搜索 | 向量化失败不阻塞，语义搜索不可用时跳过 |
| **SiliconFlow** | HTTP | 出 | Embedding 向量化 | 向量化失败时 Qdrant 索引跳过 |

## gRPC 接口（入方向）

定义在 [docretrieval.proto](../src/Contract/Protos/docretrieval.proto)：

| RPC | 请求 | 响应 | 说明 |
|-----|------|------|------|
| `ExactSearch` | `ExactSearchRequest` | `SearchResponse` | 精确关键词搜索 |
| `HybridSearch` | `HybridSearchRequest` | `SearchResponse` | 混合搜索（关键词+语义） |

## HTTP API（入方向）

定义在 [DocumentAdminEndpoints.cs](../src/Service/DocumentAdminEndpoints.cs)：

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/admin/documents/upload` | 上传文档文件（PDF/DOC/DOCX/PPT/PPTX，最大 200MB） |
| GET | `/admin/documents` | 分页列出文档 |
| GET | `/admin/documents/{id}/status` | 查询文档解析状态 |
| DELETE | `/admin/documents/{title}` | 删除文档 |
| PUT | `/admin/documents/{title}/metadata` | 更新文档元数据 |
| GET | `/admin/documents/search-test` | 搜索测试 |
| GET | `/health` | 健康检查 |

## 失败语义

### OpenSearch 写失败
- 索引文档时失败：Worker 记录 error 日志，**不标记 job 失败**，文档仍然搜索可用（通过 DB 倒排索引）
- 搜索时无法连接：`SearchDomainService` 自动回退到 PostgreSQL 倒排索引搜索

### Qdrant 写失败
- 向量索引时失败：Worker 记录 error 日志，**不阻塞流程**
- 语义搜索不可用时：HybridSearch 中 `SemanticScore` 返回默认值

### SiliconFlow Embedding 失败
- 向量化调用失败：Qdrant 索引该文档时跳过，语义搜索不覆盖该文档

### OSS 下载失败
- 导入 Worker 下载文件失败：job 标记为 `failed`，记录 `error_message`