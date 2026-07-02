# Design — 服务级架构

## 分层架构

```
┌─────────────────────────────────────────────────┐
│  Host (Ruoyu.Study.DocLibrary.Host)           │
│  Program.cs: DI, Kestrel, HTTP                  │
│  appsettings.json: 配置管理                      │
└───────────────┬─────────────────────────────────┘
                │
┌───────────────▼─────────────────────────────────┐
│  Service (Ruoyu.Study.DocLibrary.Service)     │
│  DocumentAdminEndpoints.cs: HTTP Admin API      │
│  IngestionWorker.cs: 后台导入任务                │
│  OpenSearchIndexService.cs: 搜索索引            │
│  DocumentParserService.cs: 文档解析             │
│  MinerUAgentClient.cs: MinerU Agent API 客户端（降级）  │
│  MinerUPrecisionClient.cs: MinerU Precision API 客户端  │
└───────────────┬─────────────────────────────────┘
                │
┌───────────────▼─────────────────────────────────┐
│  Domain (Ruoyu.Study.DocLibrary.Domain)       │
│  IDocumentDomainService.cs: 文档 CRUD 接口      │
│  DocumentDomainService.cs: 文档 CRUD 实现       │
│  SearchDomainService.cs: 搜索逻辑（含降级）      │
│  IDocumentParserService: 解析器接口              │
│  Repositories/: 各仓储接口（每接口一文件）       │
│  Exceptions/DocLibraryValidationException.cs   │
│  Models/: 各模型（每模型一文件）                 │
└───────────────┬─────────────────────────────────┘
                │
┌───────────────▼─────────────────────────────────┐
│  Database (Ruoyu.Study.DocLibrary.Database)   │
│  Entities/: 6 个 EF Core Entity                  │
│  Repositories/: Repository 实现                  │
│  DocLibraryDbContext.cs: EF DbContext          │
│  DatabaseInitializer.cs: SQL 初始化              │
└─────────────────────────────────────────────────┘
```

> **注**: Contract 层（gRPC proto 定义）已于 2026-07 移除。搜索功能已迁移至 HTTP 端点 `GET /admin/documents/search`。

## 技术栈

| 组件 | 技术 | 说明 |
|------|------|------|
| 框架 | .NET 8 | ASP.NET Core HTTP |
| 通信 | HTTP REST (JSON) | Admin API + 搜索 |
| ORM | EF Core 8.0 | Npgsql + SQLite |
| 数据库 | PostgreSQL / SQLite | 双数据库切换 |
| 对象映射 | Mapster 10.0 | Entity ↔ Model 映射 |
| PDF 解析 | PdfPig 0.1 | PDF 文本提取 |
| DOCX 解析 | DocumentFormat.OpenXml 3.2 | Word 文档文本提取 |
| 搜索引擎 | OpenSearch 2.19 (Docker) / OpenSearch.Net 1.8 (NuGet) | 外部全文检索 |
| 测试 | xUnit + Moq + FluentAssertions | 三层测试 |

## 认证架构

### 方案：JWT Bearer（QuantumZhou.Identity 签发）

DocLibrary 的 Admin API 通过 JWT Bearer Token 进行认证，Token 由 QuantumZhou.Identity 服务签发。

```
┌──────────┐  1. POST /admin/auth/login    ┌──────────────────┐
│  Admin   │ ──────────────────────────────► │  DocLibrary    │
│  前端    │                                 │  (AuthEndpoints) │
│          │  2. 返回 JWT + RefreshToken     │                  │
│          │ ◄────────────────────────────── │                  │
│          │                                 │                  │
│          │  3. Authorization: Bearer <JWT> │                  │
│          │ ──────────────────────────────► │  .RequireAuthorization()  │
│          │                                 │  JWT 验证（JWKS） │
└──────────┘                                 └────────┬─────────┘
                                                      │
                                             4. gRPC GetToken
                                             (password grant_type,
                                              AppId 可选用于审计)
                                                      │
                                                      ▼
                                             ┌──────────────────┐
                                             │  QuantumZhou     │
                                             │  Identity        │
                                             │  (gRPC :5001)    │
                                             │                  │
                                             │  /.well-known/   │
                                             │  jwks (公钥)     │
                                             └──────────────────┘
```

### 认证流程

1. **登录**：前端发送用户名/密码到 `POST /admin/auth/login`，DocLibrary 后端通过 gRPC 调用 Identity 的 `GetToken`（password grant_type），验证成功后将 JWT 和 RefreshToken 返回给前端
2. **请求**：前端在每次 API 请求中携带 `Authorization: Bearer <JWT>`，ASP.NET Core 通过 Identity 的 JWKS 端点验证 JWT 签名
3. **刷新**：JWT 过期前，前端通过 `POST /admin/auth/refresh` 使用 RefreshToken 获取新 JWT
4. **登出**：前端清除本地 Token 存储，可选调用 `POST /admin/auth/logout` 通知 Identity 吊销 RefreshToken

### 配置项

| 配置键 | 说明 | 示例 |
|--------|------|------|
| `Identity:GrpcEndpoint` | Identity gRPC 地址 | `http://localhost:5001` |
| `Identity:AppId` | 应用标识，仅用于审计日志和 RefreshToken 标记（password 流程不校验） | （未配置，留空） |
| `Identity:AppSecret` | 应用密钥，password 流程不使用 | （未配置） |
| `Jwt:Issuer` | JWT 签发者（与 Identity 一致） | `QuantumZhou.Identity` |
| `Jwt:Audience` | JWT 受众（与 Identity 一致） | `QuantumZhou.microservices` |
| `Jwt:JwksEndpoint` | JWKS 公钥端点 | `http://localhost:5002/.well-known/jwks` |

## 关键设计决策

| 决策 | 理由 |
|------|------|
| OpenSearch 不可用时回退数据库搜索 | 确保搜索服务高可用 |
| 异步解析（Worker 模式） | 解析耗时，避免阻塞上传请求 |
| SQLite 支持本地开发 | 无需 PostgreSQL 即可本地运行 |
| 原生 SQL 建表（非 Migration） | 简化部署，避免 Migration 版本冲突 |
| OSS 支持 LocalFile / S3 切换 | 环境变量 `USE_LOCAL_OSS` 控制 |
| JWT Bearer 认证（Identity 签发） | 统一认证中心，微服务间标准方案 |
| MinerU Precision API 在线解析 | 含图片输出，Token 认证，每日 1000 页免费额度 |
| 移除 gRPC，统一使用 HTTP REST | DocLibrary 为低并发管理服务，gRPC 无性能优势且增加维护成本；无外部 gRPC 消费者 |

##### 关键源文件

| 文件 | 用途 |
|------|------|
| [Program.cs](../../src/Host/Program.cs) | 服务启动配置 + `AddIdentityClient()` 调用 |
| [IDocumentDomainService.cs](../../src/Domain/Services/IDocumentDomainService.cs) | 文档领域接口 |
| [DocumentDomainService.cs](../../src/Domain/Services/DocumentDomainService.cs) | 文档领域实现 |
| [SearchDomainService.cs](../../src/Domain/Services/SearchDomainService.cs) | 搜索领域逻辑 |
| [IngestionWorker.cs](../../src/Service/IngestionWorker.cs) | 后台导入 |
| [DocumentAdminEndpoints.cs](../../src/Service/DocumentAdminEndpoints.cs) | HTTP API（含搜索） |
| [MinerUAgentClient.cs](../../src/Service/MinerUAgentClient.cs) | MinerU Agent API 客户端（降级方案，无图片） |
| [MinerUPrecisionClient.cs](../../src/Service/MinerUPrecisionClient.cs) | MinerU Precision API 客户端（含图片） |
| [DatabaseInitializer.cs](../../src/Database/DatabaseInitializer.cs) | 表初始化 |