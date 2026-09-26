# Integration — 集成矩阵

## 集成总表

| 目标 | 协议 | 方向 | 用途 | 安全边界 |
|------|------|------|------|----------|
| Admin UI | HTTP 同源 | 入 | 文档管理和搜索 | Identity `role=admin` HttpOnly Cookie/JWT |
| SignaCore | HTTP/OIDC | 出 | 登录、刷新、撤销、JWKS | Authority 来自 Consul |
| PostgreSQL | TCP | 出 | 文档/解析数据 CRUD | 服务私有数据库 |
| StructaDoc | HTTP | 出 | 文档上传、Parse Run、Blocks/Markdown/Assets 同步、图片代理、删除（ADR-0009） | `Authorization: ApiKey` scoped key，BaseUrl 来自 Consul/环境变量 |
| MinIO/SeaweedFS | S3 | 出 | 存量文件与解析图片（只读兼容与删除清理） | 共享 Consul 配置 |
| OpenSearch | HTTP | 出 | block 索引和查询 | 共享 Consul 配置 |
| LLM（OpenAI 兼容，可选） | HTTP | 出 | 元数据分析 | 私有 ApiKey |

当前不存在 Quaestura → Doctheca 集成。后续出现真实调用需求时，必须独立
定义数据契约、认证方式和部署配置，不复用管理员 Cookie，也不预留未使用接口。

## 管理 API

除 `/admin/auth/login`、`/admin/auth/refresh`、`/admin/auth/logout` 外，所有
`/admin/*` 端点要求 `DocthecaAdmin` 策略。完整认证契约见
[AdminAuthentication](../modules/AdminAuthentication/01-FEATURE.md)。

主要管理端点：

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/admin/document-files/upload` | 上传文档 |
| GET | `/admin/document-files` | 文档列表 |
| GET | `/admin/document-files/{id}` | 文档详情 |
| PUT | `/admin/document-files/{id}/metadata` | 更新元数据 |
| POST | `/admin/document-files/{id}/parse` | 触发解析 |
| DELETE | `/admin/document-files/{id}` | 删除文档 |
| GET/DELETE | `/admin/document-parses...` | 解析列表和删除 |
| GET | `/admin/document-parses/{parseId}/images/{imageId}/content` | StructaDoc 解析图片代理（浏览器经 admin Cookie 认证） |
| GET | `/admin/documents/search` | 搜索 |
| GET | `/admin/document-files|document-parses/.../export/*` | 导出 |

## 匿名入口

- `/`、静态资源和 SPA fallback
- `/health`
- `/admin/auth/login`
- `/admin/auth/refresh`
- `/admin/auth/logout`

## 失败语义

| 类别 | 行为 |
|------|------|
| 未认证管理请求 | 401 |
| 已认证但非管理员 | 403 |
| Identity 不可用 | 登录/刷新返回受控 502/401，不泄露内部异常 |
| OpenSearch 写失败 | 不阻塞解析主流程，记录 Warning |
| OpenSearch 查询失败 | 返回空结果并记录 Warning |
