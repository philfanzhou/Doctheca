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

### 第 2 代任务（演进 V1，block 结构化检索）

| ID | 任务 | 状态 | 验证文件 |
|----|------|------|---------|
| DS-13 | 扩展 `OpenSearchIndexService.IndexParseBlocksAsync`：在同一 bulk 追加 minerU 维度字段（`x0`/`y0`/`x1`/`y1`/`score`/`has_image`/`_meta.block_data`）；扩展 `BuildIndexBody` 追加 minerU 维度映射 | planned | `OpenSearchIndexService.cs` |
| DS-14 | 扩展 `MinerUFileParseWorker.IndexBlocksToSearchAsync`：调用扩展后的 `IndexParseBlocksAsync`（同一 best-effort 入口） | planned | `MinerUFileParseWorker.cs` |
| DS-15 | 扩展 `SearchResultModel`（追加 optional `BlockData`/`Bbox`/`Score`）+ `SearchFilterModel`（追加 `BlockType`/`PageNumber`/`ParseId`/`DocumentFileId`/`HasImage`） | planned | `SearchResultModel.cs` / `SearchFilterModel.cs` |
| DS-16 | 扩展 `OpenSearchIndexService.ExactSearchAsync`（`BuildSearchBody` 追加 minerU filter 分支 + `ParseSearchResponse` 追加 minerU 回挂分支） | planned | `OpenSearchIndexService.cs` |
| DS-17 | 扩展 `SearchDomainService.ExactSearchAsync`：透传 minerU filter + 解析回挂字段（同一方法内扩展，复用 V1 降级模式） | planned | `SearchDomainService.cs` |
| DS-18 | 扩展 `DocumentSearchEndpoints.cs`：同一 `GET /admin/documents/search` 追加可选 minerU 入参（V1 校验保留；参数 null 时零回归） | planned | `DocumentSearchEndpoints.cs` |
| DS-19 | doclibrary 自带前端扩展 `SearchPage.vue`：加"高级筛选"抽屉 + 结果行展开 `blockData`/`bbox`/`score` 详情（改造现有页，不新增 `BlockSearchPage.vue`） | planned | `SearchPage.vue` |
| DS-20 | 单元测试追加：在现有 `OpenSearchIndexServiceTests` / `SearchDomainServiceTests` 追加 minerU filter 构造 / `blockData` 回挂 / 缺省 fallback / 零回归断言（不新增测试方法类） | planned | 测试文件 |

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

DocumentSearchEndpoints.Search（第 2 代扩展）
  └── SearchDomainService.ExactSearchAsync（同一方法内 minerU filter 分支 + 回挂解析）
        └── OpenSearchIndexService.ExactSearchAsync（同一方法内 minerU filter + 回挂）
              ├── BuildSearchBody（第 2 代追加 minerU filter 子句；无 minerU filter 时输出等同 V1）
              ├── OpenSearchLowLevelClient.SearchAsync
              └── ParseSearchResponse（第 2 代追加 minerU 回挂字段映射）

MinerUFileParseWorker.IndexBlocksToSearchAsync
  └── OpenSearchIndexService.IndexParseBlocksAsync（第 2 代同一 bulk 追加 minerU 维度字段）
        ├── IDocumentParseBlockRepository.GetByParseIdAsync
        └── OpenSearchLowLevelClient.BulkAsync   （V1 6 个方法签名不变）

Program.cs (启动)
  └── OpenSearchIndexService.EnsureIndexAsync (best-effort)
        └── BuildIndexBody (第 2 代追加 minerU 维度映射后仍由同一方法生成)
```
