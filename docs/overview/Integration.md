# Integration — 集成矩阵

## 集成总表

| 目标 | 协议 | 方向 | 用途 | 安全边界 |
|------|------|------|------|----------|
| Admin UI | HTTP 同源 | 入 | 文档管理和搜索 | Identity `role=admin` HttpOnly Cookie/JWT |
| QuestionBank | HTTP | 入 | 只读拉取解析结果 | 专用服务密钥 |
| QuantumZhou.Identity | HTTP/OIDC | 出 | 登录、刷新、撤销、JWKS | Authority + 可选 AppId/AppSecret |
| PostgreSQL | TCP | 出 | 文档/解析数据 CRUD | 服务私有数据库 |
| MinIO/SeaweedFS | S3 | 出 | 文件和图片 | 服务私有凭据 |
| OpenSearch | HTTP | 出 | block 索引和查询 | 内部网络 |
| MinerU/doc-converter/LLM | HTTP | 出 | 解析与元数据分析 | 各自私有凭据 |

## 管理 API

除 `/admin/auth/login`、`/admin/auth/refresh`、`/admin/auth/logout` 外，所有 `/admin/*` 端点要求 `DocLibraryAdmin` 策略。完整认证契约见 [AdminAuthentication](../modules/AdminAuthentication/01-FEATURE.md)。

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
| GET | `/admin/documents/search` | 搜索 |
| GET | `/admin/document-files|document-parses/.../export/*` | 导出 |

## QuestionBank 只读 API

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/internal/question-bank/document-parses` | 查询 parsed parse |
| GET | `/internal/question-bank/document-parses/{parseId}/blocks` | 查询结构化 blocks |
| GET | `/internal/question-bank/images/{imageId}` | 获取图片 |

QuestionBank 调用必须携带 `X-DocLibrary-Service-Key`。DocLibrary 不提供导入状态回写；QuestionBank 以 `parseId` 为来源幂等键，在自身事务中防止重复导入。

详细规格见 [QuestionBankImport](../modules/QuestionBankImport/01-FEATURE.md)。

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
| 内部服务密钥缺失或错误 | 401 |
| OpenSearch 写失败 | 不阻塞解析主流程，记录 Warning |
| OpenSearch 查询失败 | 返回空结果并记录 Warning |
| QuestionBank 图片不存在 | 404 + `DOCLIBRARY_IMAGE_NOT_FOUND` |
| QuestionBank OSS 下载失败 | 500 + `DOCLIBRARY_OSS_DOWNLOAD_FAILED` |
