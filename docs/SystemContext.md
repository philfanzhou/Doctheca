# SystemContext — 服务定位

## 服务定位

`ruoyu.docretrieval` 是 Ruoyu.Study 平台的**文档检索微服务**，负责教育文档的存储、解析、全文搜索和语义搜索。

## 上下游关系

```
┌──────────────┐     ┌──────────────┐
│  Admin UI    │     │  Third-party │
│  (HTTP)      │     │  Clients    │
└──────┬───────┘     └──────┬───────┘
       │ HTTP               │ gRPC (5011)         ┌──────────────────┐
       │ (5012)             │ DocumentRetrieval   │  SiliconFlow     │
       ▼                    ▼     Service         │  Embedding API   │
┌─────────────────────────────────────────┐       │  (HTTP)          │
│           DocRetrieval                  │──────►│                  │
│                                         │       └──────────────────┘
│  ┌─────────────────────────────────┐    │
│  │  gRPC: ExactSearch              │    │       ┌──────────────────┐
│  │  gRPC: HybridSearch             │◄───┤       │  OpenSearch      │
│  │  HTTP: UploadDocument           │    │──────►│  (搜索索引)      │
│  │  HTTP: ListDocuments            │    │       └──────────────────┘
│  │  HTTP: DeleteDocument           │    │
│  │  HTTP: UpdateDocumentMetadata   │    │       ┌──────────────────┐
│  │  HTTP: GetDocumentStatus        │    │──────►│  Qdrant          │
│  │  Worker: IngestionWorker        │    │       │  (向量搜索)      │
│  └─────────────────────────────────┘    │       └──────────────────┘
│                                         │
│  ┌─────────────────────────────────┐    │
│  │  PostgreSQL                     │    │       ┌──────────────────┐
│  │  ruoyu_study_docretrieval       │    │──────►│  MinIO / SeaweedFS│
│  │  (文档 + 倒排索引)              │    │       │  LocalFile (OSS) │
│  └─────────────────────────────────┘    │       └──────────────────┘
└─────────────────────────────────────────┘
```

## 上游调用方

| 调用方 | 协议 | 用途 |
|--------|------|------|
| Admin UI 前端 | HTTP (5012) | 文档上传、列表、删除、元数据更新 |
| 其他微服务 | gRPC (5011) | 精确搜索、混合搜索（语义+关键词） |

## 下游依赖

| 依赖 | 类型 | 用途 |
|------|------|------|
| PostgreSQL | 数据库 | 文档存储、倒排索引、搜索 |
| MinIO / SeaweedFS / LocalFile | OSS | 文件存储（PDF/DOCX） |
| OpenSearch | 搜索引擎 | 外部全文检索索引（可选回退到数据库） |
| Qdrant | 向量数据库 | 语义搜索的向量索引 |
| SiliconFlow API | Embedding | 文本向量化（HTTP 外部 API） |

## 端口分配

| 端口 | 协议 | 用途 |
|------|------|------|
| 5011 | gRPC (HTTP/2) | DocumentRetrieval gRPC 服务 |
| 5012 | HTTP (HTTP/1.1) | Admin API + 静态文件 + Health Check |

## 服务边界

- **负责**：文档上传、解析（PDF/DOCX→结构化数据）、全文/语义搜索
- **不负责**：用户管理、题目管理、错题管理（这些由其他微服务处理）
- **不调用其他 Ruoyu 微服务**：本服务是自包含的，无 gRPC 调用下游 Ruoyu 服务