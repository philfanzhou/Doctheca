# Requirements — 服务级需求

> 本文件是需求摘要入口。详细功能需求见 [modules/](../modules/README.md) 目录。

## 功能需求

### 文档管理

| 编号 | 需求 | 详见 |
|------|------|------|
| FR-01 | 上传文档：支持 PDF/DOCX 上传，文件存入 OSS，记录存入数据库 | [DocumentUpload](../modules/DocumentUpload/01-FEATURE.md) |
| FR-02 | 文档列表：分页查询文档，支持按状态/学科/年级/关键词过滤 | [DocumentList](../modules/DocumentList/01-FEATURE.md) |
| FR-03 | 删除文档：删除文档及关联的页面、片段、倒排索引、搜索索引 | [DocumentDeletion](../modules/DocumentDeletion/01-FEATURE.md) |
| FR-04 | 更新文档元数据：修改标题/学科/年级/年份等元数据 | [DocumentMetadata](../modules/DocumentMetadata/01-FEATURE.md) |
| FR-05 | 查询文档解析状态：返回文档当前 status 和 job 信息 | [DocumentList](../modules/DocumentList/01-FEATURE.md) |

### 文档解析

| 编号 | 需求 | 详见 |
|------|------|------|
| FR-06 | 文档解析：异步解析 PDF/DOCX 为页面→片段→题目→Token 结构化数据 | [DocumentParsing](../modules/DocumentParsing/01-FEATURE.md) |
| FR-07 | 后台导入：IngestionWorker 轮询 pending job 并执行解析+索引 | [DocumentUpload](../modules/DocumentUpload/01-FEATURE.md) |

### 文档搜索

| 编号 | 需求 | 详见 |
|------|------|------|
| FR-08 | 精确搜索：关键词匹配，支持 OpenSearch 或 DB 倒排索引回退 | [ExactSearch](../modules/ExactSearch/01-FEATURE.md) |

## 非功能需求

| 编号 | 需求 |
|------|------|
| NFR-01 | 搜索响应时间 < 2s（精确搜索） |
| NFR-02 | 文档解析异步执行，不阻塞上传响应 |
| NFR-03 | OpenSearch 不可用时降级，不中断主流程 |
| NFR-04 | 支持 PostgreSQL 和 SQLite 双数据库 |
| NFR-05 | 服务双端口：gRPC(5011) + HTTP(5012) |