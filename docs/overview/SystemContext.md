# SystemContext — 服务定位

## 服务定位

`ruoyu.doclibrary` 是 Ruoyu.Study 平台的文档检索微服务，负责教育文档的存储、
解析、管理与全文精确搜索。服务在 HTTP 5012 上同时托管 Vue 管理前端、
管理 API 和健康检查。

## 调用关系

```text
Admin Browser
  ├─ anonymous SPA/login
  └─ Identity admin HttpOnly session
          │
          ▼
DocLibrary :5012 ─────► SignaCore :5002
  │                    password/refresh/revoke + OIDC/JWKS
  ├────► PostgreSQL
  ├────► SeaweedFS/MinIO（仅存量对象）
  ├────► OpenSearch
  ├────► StructaDoc :8080（文档上传与解析，ADR-0009）
  └────► optional LLM（元数据分析）
```

## 上游调用方

| 调用方 | 入口 | 身份 | 用途 |
|--------|------|------|------|
| Admin UI | `/`、`/admin/*` | Identity `role=admin` Cookie/JWT | 文件、解析、元数据、导出和搜索管理 |

普通 Identity 用户没有管理员角色，不能登录或调用管理 API。当前 DocLibrary
不提供 Quaestura 专用 HTTP 接口；如果后续出现真实调用方，必须重新设计
数据契约和服务认证。

## 下游依赖

| 依赖 | 用途 |
|------|------|
| SignaCore | 密码登录、Token 刷新/撤销、OIDC discovery/JWKS |
| PostgreSQL | 文件、解析记录和本地同步的 blocks/images |
| StructaDoc（外部仓库，:8080） | 文档原件与解析产物主责存储；Parse Run 执行（MinerU Provider + LibreOffice 转换回退）；Blocks/Markdown/Assets API |
| MinIO / SeaweedFS / LocalFile | 仅存量（迁移前上传）文件与解析图片的只读兼容与删除清理 |
| OpenSearch | 解析 block 全文索引 |
| OpenAI 兼容 LLM（可选） | 元数据分析 |

## 服务边界

- DocLibrary 独占写入自己的文档与解析记录表；新文档的原件与解析产物由 StructaDoc 主责存储，DocLibrary 只保存 documentId/parseRunId 引用和本地 blocks/images 同步副本（ADR-0009）。
- DocLibrary 不直连 StructaDoc 的数据库或对象存储，一律经其版本化 API。
- DocLibrary 不保存 Quaestura 导入状态或题目 ID。
- Identity 负责用户凭据验证和 JWT 签发；DocLibrary 只验证并消费管理员身份。
