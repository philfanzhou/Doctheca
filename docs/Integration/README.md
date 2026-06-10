# Integration — 外部系统交互

> DocRetrieval 服务目前**不通过 gRPC 调用其他 Ruoyu 微服务**，所有外部依赖均为基础设施（数据库、搜索服务、向量服务、OSS）。
>
> 详见顶层 [Integration.md](../Integration.md)。

## 外部系统列表

| 系统 | 类型 | 交互文档 |
|------|------|---------|
| PostgreSQL | 数据库 | [database/](../database/README.md) |
| MinIO / SeaweedFS / LocalFile | OSS | 配置在 [appsettings.json](../../src/Host/appsettings.json) |
| OpenSearch | 搜索引擎 | 配置在 [appsettings.json](../../src/Host/appsettings.json) |
| Qdrant | 向量数据库 | 配置在 [appsettings.json](../../src/Host/appsettings.json) |
| SiliconFlow | Embedding API | 配置在 [appsettings.json](../../src/Host/appsettings.json) |

> 以上外部系统均为基础设施组件，无需 `ExternalSystem/Feature/` 六件套文档。