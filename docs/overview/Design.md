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

## 访问控制架构

### 方案：内网管理后台，无应用层认证

DocLibrary 作为内网管理后台运行，**不实现应用层认证**：

- 所有 `/admin/*` 端点 `AllowAnonymous`，直接接受请求
- 后端无 JWT Bearer 中间件、无 Authentication / Authorization 中间件、无 Identity 集成
- 前端无登录页、无 Token 存储、无 axios 拦截器
- 访问控制由**部署层网络隔离**实现：仅内网可访问 `:5012` 端口

### 历史背景

原实现通过 JWT Bearer Token 调用 QuantumZhou.Identity 进行认证。考虑到 DocLibrary 仅为内网管理页面，无高并发与外部访问需求，应用层认证增加复杂度而无实际收益，已于 2026-07-04 移除。`AuthEndpoints.cs`、`LoginPage.vue`、`authService.ts` 已删除。

## 关键设计决策

| 决策 | 理由 |
|------|------|
| OpenSearch 不可用时回退数据库搜索 | 确保搜索服务高可用 |
| 异步解析（Worker 模式） | 解析耗时，避免阻塞上传请求 |
| SQLite 支持本地开发 | 无需 PostgreSQL 即可本地运行 |
| 原生 SQL 建表（非 Migration） | 简化部署，避免 Migration 版本冲突 |
| OSS 支持 LocalFile / S3 切换 | 环境变量 `USE_LOCAL_OSS` 控制 |
| 移除 Identity 鉴权，改为内网部署隔离 | 内网管理后台，应用层认证增加复杂度无实际收益 |
| MinerU Precision API 在线解析 | 含图片输出，Token 认证，每日 1000 页免费额度 |
| 移除 gRPC，统一使用 HTTP REST | DocLibrary 为低并发管理服务，gRPC 无性能优势且增加维护成本；无外部 gRPC 消费者 |

##### 关键源文件

| 文件 | 用途 |
|------|------|
| [Program.cs](../../src/Host/Program.cs) | 服务启动配置（无鉴权中间件） |
| [IDocumentDomainService.cs](../../src/Domain/Services/IDocumentDomainService.cs) | 文档领域接口 |
| [DocumentDomainService.cs](../../src/Domain/Services/DocumentDomainService.cs) | 文档领域实现 |
| [SearchDomainService.cs](../../src/Domain/Services/SearchDomainService.cs) | 搜索领域逻辑 |
| [IngestionWorker.cs](../../src/Service/IngestionWorker.cs) | 后台导入 |
| [DocumentAdminEndpoints.cs](../../src/Service/DocumentAdminEndpoints.cs) | HTTP API（含搜索） |
| [MinerUAgentClient.cs](../../src/Service/MinerUAgentClient.cs) | MinerU Agent API 客户端（降级方案，无图片） |
| [MinerUPrecisionClient.cs](../../src/Service/MinerUPrecisionClient.cs) | MinerU Precision API 客户端（含图片） |
| [DatabaseInitializer.cs](../../src/Database/DatabaseInitializer.cs) | 表初始化 |