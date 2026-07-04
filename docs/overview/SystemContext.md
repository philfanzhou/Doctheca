# SystemContext — 服务定位

## 服务定位

`ruoyu.doclibrary` 是 Ruoyu.Study 平台的**文档检索微服务**，负责教育文档的存储、解析和全文精确搜索。作为**内网管理后台**运行，不实现应用层认证。

## 上下游关系

```
┌──────────────┐     ┌──────────────┐
│  Admin UI    │     │  QuestionBank│
│  (HTTP)      │     │  服务 (HTTP) │
└──────┬───────┘     └──────┬───────┘
       │ HTTP               │ HTTP
       │ (5012)             │ (5012)
       ▼                    ▼
┌─────────────────────────────────────────┐
│           DocLibrary                  │
│                                         │
│  ┌─────────────────────────────────┐    │       ┌──────────────────┐
│  │  HTTP: ExactSearch              │    │──────►│  OpenSearch      │
│  │  HTTP: UploadDocument           │    │       │  (搜索索引)      │
│  │  HTTP: ListDocuments            │    │       └──────────────────┘
│  │  HTTP: DeleteDocument           │    │
│  │  HTTP: UpdateDocumentMetadata   │    │       ┌──────────────────┐
│  │  HTTP: GetDocumentStatus        │    │──────►│  MinIO / SeaweedFS│
│  │  HTTP: QuestionBankImport       │    │       │  LocalFile (OSS) │
│  │  Worker: IngestionWorker        │    │       └──────────────────┘
│  └─────────────────────────────────┘    │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │  PostgreSQL                     │    │
│  │  ruoyu_study_doclibrary         │    │
│  │  (文档 + 倒排索引)              │    │
│  └─────────────────────────────────┘    │
└─────────────────────────────────────────┘
```

## 上游调用方

| 调用方 | 协议 | 用途 |
|--------|------|------|
| Admin UI 前端 | HTTP (5012) | 文档上传、列表、删除、元数据更新 |
| QuestionBank 服务 | HTTP (5012) | 拉模式获取解析数据、回写导入状态 |
| 其他微服务 / HTTP 客户端 | HTTP (5012) | 精确搜索 |

## 下游依赖

| 依赖 | 类型 | 用途 |
|------|------|------|
| PostgreSQL | 数据库 | 文档存储、倒排索引、搜索 |
| MinIO / SeaweedFS / LocalFile | OSS | 文件存储（PDF/DOCX） |
| OpenSearch | 搜索引擎 | 外部全文检索索引（可选回退到数据库） |

> **不依赖 QuantumZhou.Identity**：内网管理后台，访问控制由部署层网络隔离实现。

## 端口分配

| 端口 | 协议 | 用途 |
|------|------|------|
| 5012 | HTTP (HTTP/1.1) | Admin API + 搜索 API + 静态文件 + Health Check |

## 服务边界

- **负责**：文档上传、解析（PDF/DOCX→结构化数据）、全文精确搜索
- **不负责**：用户管理、题目管理、错题管理（这些由其他微服务处理）
- **不调用其他 Ruoyu 微服务**：本服务作为内网管理后台，不调用任何外部认证服务，所有 `/admin/*` 端点 `AllowAnonymous`