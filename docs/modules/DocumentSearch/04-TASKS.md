# DocumentSearch — 任务清单 (TASKS)

> **本模块代码已实现完成（第 1 代 + 第 2 代后端）。** 第 2 代前端（DS-19）属独立工程，状态保持 planned。
> 以下为代码评审与自动化验证记录。

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
| DS-13 | 扩展 `OpenSearchIndexService.IndexParseBlocksAsync`：在同一 bulk 追加 minerU 维度字段（`x0`/`y0`/`x1`/`y1`/`score`/`has_image`/`sub_type`/`text_level`/`text_format`/`caption`/`_meta.block_data`）；扩展 `BuildIndexBody` 追加 minerU 维度映射 | completed | `OpenSearchIndexService.cs` |
| DS-14 | 扩展 `MinerUFileParseWorker.IndexBlocksToSearchAsync`：调用扩展后的 `IndexParseBlocksAsync`（同一 best-effort 入口）。**实现说明**：Worker 本身无需改动——minerU 字段由 `DocumentParseBlockService.ParseBlock` 抽取并写入 `DocumentParseBlockEntity`，经 `DocumentParseBlockRepository` 持久化，`IndexParseBlocksAsync` 通过 `GetByParseIdAsync` 读取后直接索引，字段流自动传递。 | completed | `MinerUFileParseWorker.cs`（无改动） |
| DS-15 | 扩展 `SearchResultModel`（追加 optional `BlockData`/`Bbox`/`MineruScore`/`SubType`/`TextLevel`/`TextFormat`/`Caption`）+ `SearchFilterModel`（追加 `BlockType`/`BlockSubType`/`PageNumber`/`TextLevel`/`TextFormat`/`ParseId`/`DocumentFileId`/`HasImage`）。**偏差说明**：SPEC §13.1.1 原拟 `float? Score`，但 V1 已有 `public double Score`（OpenSearch `_score`），C# 不允许同名字段，故第 2 代新字段命名为 `MineruScore`（与 §13.9.1 `block.MineruScore` 一致）；HTTP 响应 JSON key 为 `mineruScore`。详见 02-SPEC.md §13.1.1 偏差说明。 | completed | `SearchResultModel.cs` / `SearchFilterModel.cs` |
| DS-16 | 扩展 `OpenSearchIndexService.ExactSearchAsync`（`BuildSearchBody` 追加 minerU filter 分支 + `ParseSearchResponse` 追加 minerU 回挂分支） | completed | `OpenSearchIndexService.cs` |
| DS-17 | 扩展 `SearchDomainService.ExactSearchAsync`：透传 minerU filter + 解析回挂字段（同一方法内扩展，复用 V1 降级模式）。**实现说明**：`SearchDomainService` 为薄封装，filter 透传通过 `SearchFilterModel` 自动完成，无需改动方法体；异常降级路径已在 UT `ExactSearchAsync_WithMinerUFilterAndIndexServiceThrow_ReturnsEmpty` 覆盖。 | completed | `SearchDomainService.cs`（无改动） |
| DS-18 | 扩展 `DocumentSearchEndpoints.cs`：同一 `GET /admin/documents/search` 追加可选 minerU 入参（`blockType`/`blockSubType`/`pageNumber`/`textLevel`/`textFormat`/`parseId`/`documentFileId`/`hasImage`）；V1 校验保留；参数 null 时零回归。响应追加 `blockData`/`bbox`/`mineruScore`/`subType`/`textLevel`/`textFormat`/`caption` optional 字段。 | completed | `DocumentSearchEndpoints.cs` |
| DS-19 | doclibrary 自带前端扩展 `SearchPage.vue`：加"高级筛选"抽屉（blockType/blockSubType/pageNumber/textLevel/textFormat/parseId/documentFileId/hasImage）+ 结果表格追加 minerU 列（块类型/subType/矿工 U 置信度/textFormat/Caption）+ 行展开 blockData/bbox/mineruScore 详情卡片；`docApi.ts` `SearchResult` 追加 7 optional 字段 + `searchTest` 签名扩展 8 个可选参数（空值不透传，零回归）；`frontend-spec.md` §4.4 同步。验证：`npm run build`（vue-tsc + vite build）通过。 | completed | `SearchPage.vue` / `docApi.ts` / `frontend-spec.md` |
| DS-20 | 单元测试追加：在现有 `OpenSearchIndexServiceTests` / `SearchDomainServiceTests` / `DocumentParseBlockServiceTests` 追加 minerU filter 构造 / `blockData` 回挂 / 缺省 fallback / bbox 归一化 / 零回归断言（不新增测试方法类）。新增 15 个测试方法，全部通过（169/169）。 | completed | 测试文件 |

## 第 2 代实现附注

### 数据库 schema 变更（DatabaseInitializer 原生 SQL）
新增 9 列到 `document_parse_blocks` 表（通过 `ALTER TABLE ADD COLUMN IF NOT EXISTS`）：
`sub_type` / `text_level` / `text_format` / `bbox_x0` / `bbox_y0` / `bbox_x1` / `bbox_y1` / `score` / `caption`

> 列名 `score` 遵循 06-CONVENTIONS §103 行"真正新字段不加 mineru_ 前缀"规定；C# 属性名 `MineruScore` 避免与 V1 概念混淆。

### 验证结果
- `dotnet build src/services/ruoyu.doclibrary/src/Ruoyu.Study.DocLibrary.sln --configuration Release`：**成功**（0 错误，3 个无关 nullable 警告）
- `dotnet test ... --configuration Release`：**169/169 通过**（含 AC-17 零回归 `BuildSearchBody_WithNoMinerUFilter_OutputEqualsV1`）

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
