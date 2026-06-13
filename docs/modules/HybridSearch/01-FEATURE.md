> **已移除**：HybridSearch（语义搜索）功能已于 2026-06-12 移除。本项目当前仅支持精确搜索（ExactSearch），不再依赖 Qdrant 和 Embedding API。

# HybridSearch — 混合检索（精确+语义）

## 功能名称和一句话概括

混合检索 — Student 服务通过 gRPC 调用混合检索接口，同时利用精确匹配和语义相似度检索文档，提高召回率和相关性。

## 核心用户故事

**Student 服务混合检索文档片段**：作为 Student 服务，我希望通过 gRPC 调用混合检索接口，同时利用精确匹配和语义相似度检索文档，系统合并两路结果并去重排序后返回，让我能获得比纯精确检索更高的召回率和更相关的搜索结果。

## 关键验收条件摘要

- AC-1：与 ExactSearch 共享相同的查询词校验（空查询、长度超限、page_size 超限）。
- AC-2：`exactTopK` 默认 50，最大 200；`semanticTopK` 默认 20，最大 100。
- AC-3：优先使用 OpenSearch 混合搜索（`ISearchIndexService.HybridSearchAsync`）。
- AC-4：OpenSearch 不可用或异常时，回退数据库搜索并记录 LogWarning。
- AC-5：回退时 `exact_word` 匹配类型的 `MatchType` 降级为 `stem_match`。
- AC-6：回退搜索结果与 ExactSearch 的数据库回退逻辑相同（segments + questions 两表匹配）。

## 明确列出"范围外"（不做什么）

- 不实现独立的语义搜索接口（语义搜索仅作为混合检索的一部分）。
- 不实现搜索结果的个性化排序或用户偏好加权。
- 不处理查询意图识别或查询改写。
- 不实现搜索结果缓存。
- 不实现搜索历史或搜索建议。
- 不处理文档权限或访问控制。

## 关键代码参考

| 组件 | 文件路径 |
|------|---------|
| DocumentRetrievalServiceImpl | `src/Service/DocumentRetrievalServiceImpl.cs` |
| SearchDomainService | `src/Domain/Services/SearchDomainService.cs` |
| ISearchIndexService | `src/Domain/Repositories/ISearchIndexService.cs` |
| OpenSearchIndexService | `src/Service/OpenSearchIndexService.cs` |
| IQdrantService | `src/Domain/Repositories/IQdrantService.cs` |
| QdrantService | `src/Service/QdrantService.cs` |
| SearchResultModel | `src/Domain/Models/SearchConfig.cs` |
| SearchFilterModel | `src/Domain/Models/SearchConfig.cs` |
| gRPC 契约 | `src/Contract/Protos/docretrieval.proto` |
| 公共消息 | `src/Contract/Protos/docretrieval.common.proto` |

## 文档索引

| 文档 | 说明 |
|------|------|
| [01-FEATURE.md](./01-FEATURE.md) | 功能概述、用户故事、验收条件（本文档） |
| [02-SPEC.md](./02-SPEC.md) | 需求规格、验收场景、非功能需求、测试策略 |
| [03-DESIGN.md](./03-DESIGN.md) | 文件结构、接口签名、数据流、错误处理、外部依赖 |
| [04-TASKS.md](./04-TASKS.md) | JSON 任务列表、命令速查、依赖图 |
| [05-TESTS.md](./05-TESTS.md) | 单元/集成/边界测试表、骨架代码 |
| [06-CONVENTIONS.md](./06-CONVENTIONS.md) | 命名、日志、错误消息、代码风格 |
