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
DocLibrary :5012 ─────► QuantumZhou.Identity :5002
  │                    password/refresh/revoke + OIDC/JWKS
  ├────► PostgreSQL
  ├────► SeaweedFS/MinIO
  ├────► OpenSearch
  └────► MinerU / doc-converter / optional LLM
```

## 上游调用方

| 调用方 | 入口 | 身份 | 用途 |
|--------|------|------|------|
| Admin UI | `/`、`/admin/*` | Identity `role=admin` Cookie/JWT | 文件、解析、元数据、导出和搜索管理 |

普通 Identity 用户没有管理员角色，不能登录或调用管理 API。当前 DocLibrary
不提供 QuestionBank 专用 HTTP 接口；如果后续出现真实调用方，必须重新设计
数据契约和服务认证。

## 下游依赖

| 依赖 | 用途 |
|------|------|
| QuantumZhou.Identity | 密码登录、Token 刷新/撤销、OIDC discovery/JWKS |
| PostgreSQL | 文件和解析数据 |
| MinIO / SeaweedFS / LocalFile | 源文件与解析图片 |
| OpenSearch | 解析 block 全文索引 |
| MinerU Precision API | 文档解析 |
| doc-converter | Office 文档转 PDF |
| OpenAI 兼容 LLM（可选） | 元数据分析 |

## 服务边界

- DocLibrary 独占写入自己的文档与解析数据。
- DocLibrary 不保存 QuestionBank 导入状态或题目 ID。
- Identity 负责用户凭据验证和 JWT 签发；DocLibrary 只验证并消费管理员身份。
