# Design — 服务级架构

## 分层架构

```
┌─────────────────────────────────────────────────┐
│  Host (Ruoyu.Study.DocRetrieval.Host)           │
│  Program.cs: DI, Kestrel, gRPC, HTTP            │
│  appsettings.json: 配置管理                      │
└───────────────┬─────────────────────────────────┘
                │
┌───────────────▼─────────────────────────────────┐
│  Service (Ruoyu.Study.DocRetrieval.Service)     │
│  DocumentRetrievalServiceImpl.cs: gRPC 实现     │
│  DocumentAdminEndpoints.cs: HTTP Admin API      │
│  IngestionWorker.cs: 后台导入任务                │
│  OpenSearchIndexService.cs: 搜索索引            │
│  QdrantService.cs: 向量索引                     │
│  DocumentParserService.cs: 文档解析             │
└───────────────┬─────────────────────────────────┘
                │
┌───────────────▼─────────────────────────────────┐
│  Domain (Ruoyu.Study.DocRetrieval.Domain)       │
│  DocumentDomainService.cs: 文档 CRUD 逻辑       │
│  SearchDomainService.cs: 搜索逻辑（含降级）      │
│  IDocumentParserService: 解析器接口              │
│  IRepositories: 仓储接口                         │
│  Models: DocumentModels, SearchConfig, Constants │
└───────────────┬─────────────────────────────────┘
                │
┌───────────────▼─────────────────────────────────┐
│  Database (Ruoyu.Study.DocRetrieval.Database)   │
│  Entities/: 6 个 EF Core Entity                  │
│  Repositories/: Repository 实现                  │
│  DocRetrievalDbContext.cs: EF DbContext          │
│  DatabaseInitializer.cs: SQL 初始化              │
└───────────────┬─────────────────────────────────┘
                │
┌───────────────▼─────────────────────────────────┐
│  Contract (Ruoyu.Study.DocRetrieval.Contract)   │
│  Protos/docretrieval.proto: gRPC 服务定义       │
│  Protos/docretrieval.common.proto: 公共消息      │
└─────────────────────────────────────────────────┘
```

## 技术栈

| 组件 | 技术 | 说明 |
|------|------|------|
| 框架 | .NET 8 | ASP.NET Core gRPC + HTTP |
| 通信 | gRPC (protobuf) | DocumentRetrieval 服务 |
| ORM | EF Core 8.0 | Npgsql + SQLite |
| 数据库 | PostgreSQL / SQLite | 双数据库切换 |
| 对象映射 | Mapster 10.0 | Entity ↔ Model 映射 |
| PDF 解析 | PdfPig 0.1 | PDF 文本提取 |
| DOCX 解析 | DocumentFormat.OpenXml 3.2 | Word 文档文本提取 |
| 搜索引擎 | OpenSearch 1.8 | 外部全文检索 |
| 向量数据库 | Qdrant.Client 1.12 | 语义搜索 |
| 测试 | xUnit + Moq + FluentAssertions | 三层测试 |

## 关键设计决策

| 决策 | 理由 |
|------|------|
| OpenSearch 不可用时回退数据库搜索 | 确保搜索服务高可用 |
| 异步解析（Worker 模式） | 解析耗时，避免阻塞上传请求 |
| SQLite 支持本地开发 | 无需 PostgreSQL 即可本地运行 |
| 原生 SQL 建表（非 Migration） | 简化部署，避免 Migration 版本冲突 |
| OSS 支持 LocalFile / S3 切换 | 环境变量 `USE_LOCAL_OSS` 控制 |

## 关键源文件

| 文件 | 用途 |
|------|------|
| [Program.cs](../../src/Host/Program.cs) | 服务启动配置 |
| [docretrieval.proto](../../src/Contract/Protos/docretrieval.proto) | gRPC 契约 |
| [DocumentDomainService.cs](../../src/Domain/Services/DocumentDomainService.cs) | 文档领域逻辑 |
| [SearchDomainService.cs](../../src/Domain/Services/SearchDomainService.cs) | 搜索领域逻辑 |
| [DocumentRetrievalServiceImpl.cs](../../src/Service/DocumentRetrievalServiceImpl.cs) | gRPC 实现 |
| [IngestionWorker.cs](../../src/Service/IngestionWorker.cs) | 后台导入 |
| [DocumentAdminEndpoints.cs](../../src/Service/DocumentAdminEndpoints.cs) | HTTP API |
| [DatabaseInitializer.cs](../../src/Database/DatabaseInitializer.cs) | 表初始化 |