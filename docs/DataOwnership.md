# DataOwnership — 数据主责与引用边界

## 本服务拥有的数据实体（主责）

以下数据由 DocRetrieval 服务独立管理，其他服务不可直接写入：

| 实体表 | 说明 | 操作权限 |
|--------|------|---------|
| `documents` | 文档主表 | 本服务独写 |
| `document_pages` | 文档页面 | 本服务独写 |
| `document_segments` | 文本片段 | 本服务独写 |
| `question_segments` | 题目片段 | 本服务独写 |
| `document_occurrences` | 倒排索引 | 本服务独写 |
| `document_ingestion_jobs` | 导入任务 | 本服务独写 |

## 本服务引用的外部数据（只读引用）

| 外部数据 | 来源 | 读取方式 | 说明 |
|---------|------|---------|------|
| （无跨服务数据引用） | — | — | DocRetrieval 不读取其他 Ruoyu 微服务的数据 |

## 外部系统依赖

| 系统 | 写入 | 读取 | 说明 |
|------|------|------|------|
| PostgreSQL | CRUD 所有表 | 全文搜索、文档查询 | 自建数据库 |
| MinIO / SeaweedFS | 上传文件 | 下载文件 | OSS 对象存储 |
| OpenSearch | 创建/删除索引 | 搜索查询 | 外部检索引擎 |
| Qdrant | 插入/删除向量 | 语义搜索 | 外部向量数据库 |
| SiliconFlow | 无 | Embedding API | 外部 AI API |

## 双写禁区

- 本服务不写入任何其他 Ruoyu 微服务的数据库
- 其他服务不应直接写入本服务的 6 张表
- OSS 文件路径格式由本服务内部控制，其他服务不应直接操作