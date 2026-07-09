# DocumentSearch — 任务清单 (TASKS)

> **本模块代码已实现完成。** 以下为代码评审与自动化验证记录。

## 任务拆解

| ID | 任务 | 状态 | 验证文件 |
|----|------|------|---------|
| DS-01 | 索引 mapping 定义（`BuildIndexBody`）：确认 blocks 字段、分析器、无 legacy 字段 | completed | `OpenSearchIndexService.cs` |
| DS-02 | 索引写入（`IndexParseBlocksAsync`）：bulk 构建、空 text 跳过、幂等 `_id` | completed | `OpenSearchIndexService.cs` |
| DS-03 | 索引删除（`DeleteParseIndexAsync` / `DeleteDocumentFileIndexAsync`）：`delete_by_query` | completed | `OpenSearchIndexService.cs` |
| DS-04 | 元数据同步（`UpdateDocumentFileMetadataAsync`）：`update_by_query` + script | completed | `OpenSearchIndexService.cs` |
| DS-05 | 搜索查询构建（`BuildSearchBody`）：phrase/match、filter、search_after、sort、highlight | completed | `OpenSearchIndexService.cs` |
| DS-06 | 搜索响应解析（`ParseSearchResponse`）：字段映射、nextToken、highlight 优先 | completed | `OpenSearchIndexService.cs` |
| DS-07 | 搜索领域服务（`SearchDomainService`）：委托调用 + 异常降级 | completed | `SearchDomainService.cs` |
| DS-08 | HTTP 端点（`DocumentSearchEndpoints`）：参数校验、filter 构造、响应格式 | completed | `DocumentSearchEndpoints.cs` |
| DS-09 | Worker 集成：解析完成后索引（best-effort）、LLM 元数据同步 | completed | `MinerUFileParseWorker.cs` |
| DS-10 | 端点集成：删除 parse/文件后清理索引、元数据更新后同步 | completed | `DocumentParseEndpoints.cs` / `DocumentFileEndpoints.cs` |
| DS-11 | 启动初始化：`EnsureIndexAsync`（best-effort） | completed | `Program.cs` |
| DS-12 | 单元测试：`OpenSearchIndexServiceTests` + `SearchDomainServiceTests` | completed | 测试文件 |

## 命令速查

```bash
# 构建
dotnet build src/services/ruoyu.doclibrary/Ruoyu.Study.DocLibrary.sln --configuration Release

# 运行本模块相关测试
dotnet test src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests \
  --configuration Release \
  --filter "FullyQualifiedName~OpenSearchIndexServiceTests|FullyQualifiedName~SearchDomainServiceTests"

# 全部测试
dotnet test src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests --configuration Release
```

## 依赖图

```
DocumentSearchEndpoints.Search
  └── SearchDomainService.ExactSearchAsync
        └── OpenSearchIndexService.ExactSearchAsync
              ├── BuildSearchBody (internal static)
              ├── OpenSearchLowLevelClient.SearchAsync
              └── ParseSearchResponse (internal static)

MinerUFileParseWorker.PersistParseResultAsync
  └── IndexBlocksToSearchAsync (best-effort)
        └── OpenSearchIndexService.IndexParseBlocksAsync
              ├── IDocumentParseBlockRepository.GetByParseIdAsync
              └── OpenSearchLowLevelClient.BulkAsync

DocumentParseEndpoints.DeleteDocumentParse
  └── OpenSearchIndexService.DeleteParseIndexAsync (best-effort)

DocumentFileEndpoints.DeleteDocumentFile
  └── OpenSearchIndexService.DeleteDocumentFileIndexAsync (best-effort)

DocumentFileEndpoints.UpdateDocumentFileMetadata
  └── OpenSearchIndexService.UpdateDocumentFileMetadataAsync (best-effort)

Program.cs (启动)
  └── OpenSearchIndexService.EnsureIndexAsync (best-effort)
        └── BuildIndexBody (internal static)
```
