# ExactSearch — 精确关键词检索

## 功能名称和一句话概括

精确关键词检索 — 通过 HTTP API 调用精确检索接口，根据关键词在文档库中搜索匹配的文本片段，获取文档名、页码、上下文文本和匹配位置。

## 核心用户故事

**Student 服务精确检索文档片段**：作为 Student 服务，我希望通过 HTTP API 调用精确检索接口，根据关键词在文档库中搜索匹配的文本片段，系统返回文档名、页码、上下文文本、匹配类型和偏移位置，让我能快速定位到用户所需的文档内容。

## 关键验收条件摘要

- AC-1：查询词为空时返回 `400 Bad Request (DOCLIBRARY_QUERY_REQUIRED)`。
- AC-2：查询词超过 200 字符时返回 `400 Bad Request (DOCLIBRARY_QUERY_TOO_LONG)`。
- AC-3：`page_size` 超过 100 时静默截断为 100（不报错）。
- AC-4：`page_size` 默认 20，最小 1，最大 100。
- AC-5：使用 OpenSearch BM25 搜索（`ISearchIndexService.ExactSearchAsync`）。
- AC-6：OpenSearch 不可用或异常时，返回空结果并记录 LogWarning（不中断请求）。
- AC-7：短语匹配时 `Score = 1.0`，`MatchType = "exact_phrase"`。
- AC-8：单词匹配时 `MatchType = "stemmed"`（`english_custom` 分析器含词干提取）。
- AC-9：游标分页使用 `search_after`，`page_token` 为上一页最后一条记录的 sort 数组 Base64 编码。
- AC-10：`SearchFilter` 中空字符串字段视为不筛选。

## 明确列出"范围外"（不做什么）

- 不实现混合排序或跨索引合并。
- 不处理查询词的分词或同义词扩展。
- 不实现搜索结果缓存。
- 不实现搜索历史或搜索建议。
- 不处理文档权限或访问控制。
- 不实现数据库回退搜索（OpenSearch 不可用时仅返回空结果）。

## 关键代码参考

| 组件 | 文件路径 |
|------|---------|
| DocumentSearchEndpoints | `src/Service/Endpoints/DocumentSearchEndpoints.cs` |
| SearchDomainService | `src/Domain/Services/SearchDomainService.cs` |
| ISearchIndexService | `src/Domain/Repositories/ISearchIndexService.cs` |
| OpenSearchIndexService | `src/Service/OpenSearchIndexService.cs` |
| SearchResultModel | `src/Domain/Models/SearchConfig.cs` |
| SearchFilterModel | `src/Domain/Models/SearchConfig.cs` |

## 文档索引

| 文档 | 说明 |
|------|------|
| [01-FEATURE.md](./01-FEATURE.md) | 功能概述、用户故事、验收条件（本文档） |
| [02-SPEC.md](./02-SPEC.md) | 需求规格、验收场景、非功能需求、测试策略 |
| [03-DESIGN.md](./03-DESIGN.md) | 文件结构、接口签名、数据流、错误处理、外部依赖 |
| [04-TASKS.md](./04-TASKS.md) | JSON 任务列表、命令速查、依赖图 |
| [05-TESTS.md](./05-TESTS.md) | 单元/集成/边界测试表、骨架代码 |
| [06-CONVENTIONS.md](./06-CONVENTIONS.md) | 命名、日志、错误消息、代码风格 |
