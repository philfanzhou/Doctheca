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
│  Endpoints/DocumentFileEndpoints.cs             │
│  Endpoints/DocumentParseEndpoints.cs            │
│  Endpoints/DocumentSearchEndpoints.cs           │
│  Endpoints/DocumentExportEndpoints.cs           │
│  Endpoints/QuestionBankImportEndpoints.cs       │
│  MinerUFileParseWorker.cs: 后台解析任务          │
│  MinerUPrecisionClient.cs: MinerU Precision API 客户端  │
│  OpenSearchIndexService.cs: 搜索索引            │
└───────────────┬─────────────────────────────────┘
                │
┌───────────────▼─────────────────────────────────┐
│  Domain (Ruoyu.Study.DocLibrary.Domain)       │
│  Services/DocumentFileService.cs: 文件管理       │
│  Services/DocumentParseService.cs: 解析管理      │
│  Services/SearchDomainService.cs: 搜索逻辑       │
│  Services/DocumentAnalysisService.cs: LLM 元数据分析 │
│  Repositories/: 各仓储接口（每接口一文件）       │
│  Exceptions/DocLibraryValidationException.cs   │
│  Models/: 各模型（每模型一文件）                 │
└───────────────┬─────────────────────────────────┘
                │
┌───────────────▼─────────────────────────────────┐
│  Database (Ruoyu.Study.DocLibrary.Database)   │
│  Entities/: EF Core Entity                      │
│  Repositories/: Repository 实现                  │
│  DocLibraryDbContext.cs: EF DbContext          │
│  DatabaseInitializer.cs: SQL 初始化              │
└─────────────────────────────────────────────────┘
```

> **注**: Contract 层（gRPC proto 定义）已于 2026-07 移除。搜索功能迁移至 HTTP 端点 `GET /admin/documents/search`。

## 技术栈

| 组件 | 技术 | 说明 |
|------|------|------|
| 框架 | .NET 8 | ASP.NET Core HTTP |
| 通信 | HTTP REST (JSON) | Admin API + 搜索 |
| ORM | EF Core 8.0 | Npgsql + SQLite |
| 数据库 | PostgreSQL / SQLite | 双数据库切换 |
| 对象映射 | Mapster 10.0 | Entity ↔ Model 映射 |
| 文档解析 | MinerU Precision API | 在线解析（含图片输出，Token 认证） |
| 搜索引擎 | OpenSearch 2.19 (Docker) / OpenSearch.Net 1.8 (NuGet) | 外部全文检索 |
| LLM | OpenAI 兼容 | 文档元数据（subject/grade/year）分析，best-effort |
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
| 异步解析（Worker 模式） | MinerU 解析耗时，避免阻塞上传请求 |
| SQLite 支持本地开发 | 无需 PostgreSQL 即可本地运行 |
| 原生 SQL 建表（非 Migration） | 简化部署，避免 Migration 版本冲突 |
| OSS 支持 LocalFile / S3 切换 | 环境变量 `USE_LOCAL_OSS` 控制 |
| 移除 Identity 鉴权，改为内网部署隔离 | 内网管理后台，应用层认证增加复杂度无实际收益 |
| MinerU Precision API 在线解析 | 含图片输出，Token 认证，每日 1000 页免费额度 |
| 移除 gRPC，统一使用 HTTP REST | DocLibrary 为低并发管理服务，gRPC 无性能优势且增加维护成本；无外部 gRPC 消费者 |
| LLM 仅用于元数据分析 | 解析完成后对缺失的 subject/grade/year 做 best-effort 自动填充，不影响主流程 |

##### 关键源文件

| 文件 | 用途 |
|------|------|
| [Program.cs](../../src/Host/Program.cs) | 服务启动配置（无鉴权中间件） |
| [DocumentFileEndpoints.cs](../../src/Service/Endpoints/DocumentFileEndpoints.cs) | 文件上传/列表/删除/元数据更新 HTTP API |
| [DocumentParseEndpoints.cs](../../src/Service/Endpoints/DocumentParseEndpoints.cs) | 解析触发/状态查询/删除 HTTP API |
| [DocumentSearchEndpoints.cs](../../src/Service/Endpoints/DocumentSearchEndpoints.cs) | 精确搜索 HTTP API |
| [DocumentExportEndpoints.cs](../../src/Service/Endpoints/DocumentExportEndpoints.cs) | 导出 HTTP API |
| [QuestionBankImportEndpoints.cs](../../src/Service/Endpoints/QuestionBankImportEndpoints.cs) | QuestionBank 拉模式导入 HTTP API |
| [MinerUFileParseWorker.cs](../../src/Service/MinerUFileParseWorker.cs) | 后台解析 Worker（MinerU 链路） |
| [MinerUPrecisionClient.cs](../../src/Service/MinerUPrecisionClient.cs) | MinerU Precision API 客户端（含图片） |
| [SearchDomainService.cs](../../src/Domain/Services/SearchDomainService.cs) | 搜索领域逻辑 |
| [OpenSearchIndexService.cs](../../src/Service/OpenSearchIndexService.cs) | OpenSearch 索引服务（基于 parse_blocks） |
| [DatabaseInitializer.cs](../../src/Database/DatabaseInitializer.cs) | 表初始化 |
