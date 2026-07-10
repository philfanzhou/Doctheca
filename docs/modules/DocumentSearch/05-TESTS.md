# DocumentSearch — 测试计划 (TESTS)

测试工具：`xUnit` + `Moq` + `FluentAssertions`。

现有测试文件（代码中实际存在）：
- `src/Tests/Ruoyu.Study.DocLibrary.Tests/OpenSearchIndexServiceTests.cs`
- `src/Tests/Ruoyu.Study.DocLibrary.Tests/SearchDomainServiceTests.cs`

> 注：不存在 `DocumentSearchEndpointsTests.cs`（旧文档误列）。HTTP 端点无独立单元测试。

## 运行方式

```bash
dotnet test src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests \
  --configuration Release \
  --filter "FullyQualifiedName~OpenSearchIndexServiceTests|FullyQualifiedName~SearchDomainServiceTests"
```

## 单元测试清单（真实方法名）

### OpenSearchIndexServiceTests（`OpenSearchIndexServiceTests.cs`）

纯逻辑测试（`internal static` 方法），不依赖 OpenSearch HTTP。

| 测试方法 | 覆盖 |
|---------|------|
| `BuildSearchBody_WithPhraseQuery_UsesMatchPhraseOnTextExact` | phrase 查询使用 `match_phrase` + `text.exact`（FR-07） |
| `BuildSearchBody_WithStemmedQuery_UsesMatchOnText` | 单词查询使用 `match` + `text`（FR-07） |
| `BuildSearchBody_WithFullFilter_WrapsInBoolWithAllClauses` | 全字段 filter 包装 `bool.must + filter`，含 `file_name`（FR-07, FR-09） |
| `BuildSearchBody_WithNullFilter_DoesNotWrapInBool` | filter 为 null 时不包装 `bool`（FR-07） |
| `BuildSearchBody_WithPartialFilter_OnlyIncludesNonEmptyFields` | 部分空字段不参与过滤（FR-07） |
| `BuildSearchBody_WithValidPageToken_IncludesSearchAfter` | 有效 pageToken 解码为 `search_after`（FR-07, FR-09） |
| `BuildSearchBody_WithInvalidPageToken_DoesNotIncludeSearchAfter` | 无效 Base64 pageToken 不生成 `search_after`（错误处理） |
| `BuildSearchBody_WithNullOrEmptyPageToken_DoesNotIncludeSearchAfter` | null/空 pageToken 不生成 `search_after`（FR-07） |
| `BuildSearchBody_AlwaysIncludesSizeSortAndHighlight` | 始终包含 `size`/`sort`/`highlight`（FR-07） |
| `BuildSearchBody_SortUsesBlockIdNotLegacyFields` | 排序使用 `block_id`，不含 legacy 字段（FR-07） |
| `ParseSearchResponse_WithValidJson_ReturnsResultsWithBlockFields` | 正常解析：字段映射、`MatchType=Stemmed`、offset=0（FR-07, AC-06） |
| `ParseSearchResponse_WithPhraseQuery_SetsExactPhraseMatchType` | phrase 查询设置 `MatchType=ExactPhrase`（FR-07, AC-07） |
| `ParseSearchResponse_WithHighlight_UsesHighlightedText` | highlight 优先于 `_source.text`（FR-07） |
| `ParseSearchResponse_WithFullPage_ReturnsNextToken` | 满页时生成 `nextToken`（FR-07） |
| `ParseSearchResponse_WithPartialPage_ReturnsNoNextToken` | 未满页时不生成 `nextToken`（FR-07） |
| `ParseSearchResponse_WithEmptyHits_ReturnsEmptyResults` | 空命中返回空结果（FR-07） |
| `ParseSearchResponse_WithMissingFields_UsesDefaults` | `_source` 缺失字段使用默认值（FR-07） |
| `ParseSearchResponse_WithMissingScore_DefaultsToZero` | `_score` 缺失时默认为 0（FR-07） |
| `ParseSearchResponse_WithNoHitsProperty_ReturnsEmptyResults` | 无 `hits` 属性时返回空结果（健壮性） |
| `BuildIndexBody_IncludesBlockPipelineFields` | mapping 包含 blocks 字段且类型正确（FR-06） |
| `BuildIndexBody_DoesNotContainLegacyFields` | mapping 不含 legacy 字段（FR-06） |
| `BuildIndexBody_RetainsSharedFieldsAndAnalyzers` | 保留共享字段和分析器（`english_custom`/`english_phrase`/子字段 `exact`）（FR-06） |

### SearchDomainServiceTests（`SearchDomainServiceTests.cs`）

Mock `ISearchIndexService`，验证领域服务委托与降级。

| 测试方法 | 覆盖 |
|---------|------|
| `ExactSearchAsync_DelegatesToSearchIndexService` | 委托调用 `ISearchIndexService.ExactSearchAsync`（FR-07） |
| `ExactSearchAsync_PropagatesFilterToIndexService` | filter 透传给索引服务（FR-07） |
| `ExactSearchAsync_PropagatesPageToken` | pageToken 透传给索引服务（FR-07） |
| `ExactSearchAsync_WhenIndexServiceThrows_ReturnsEmptyResults` | 索引服务抛异常时返回空结果 + LogWarning（FR-08, AC-08） |
| `ExactSearchAsync_ReturnsEmptyResultsWhenNoMatch` | 无匹配时返回空结果（FR-07） |

## FR/AC 映射

| 测试方法 | 验证 FR | 验证 AC |
|---------|---------|---------|
| `BuildSearchBody_WithPhraseQuery_UsesMatchPhraseOnTextExact` | FR-07 | AC-07 |
| `BuildSearchBody_WithStemmedQuery_UsesMatchOnText` | FR-07 | AC-07 |
| `BuildSearchBody_WithFullFilter_WrapsInBoolWithAllClauses` | FR-07 | — |
| `BuildSearchBody_WithNullFilter_DoesNotWrapInBool` | FR-07 | — |
| `BuildSearchBody_WithPartialFilter_OnlyIncludesNonEmptyFields` | FR-07 | — |
| `BuildSearchBody_WithValidPageToken_IncludesSearchAfter` | FR-07 | — |
| `BuildSearchBody_WithInvalidPageToken_DoesNotIncludeSearchAfter` | 错误处理 | — |
| `BuildSearchBody_WithNullOrEmptyPageToken_DoesNotIncludeSearchAfter` | FR-07 | — |
| `BuildSearchBody_AlwaysIncludesSizeSortAndHighlight` | FR-07 | — |
| `BuildSearchBody_SortUsesBlockIdNotLegacyFields` | FR-07 | — |
| `ParseSearchResponse_WithValidJson_ReturnsResultsWithBlockFields` | FR-07 | AC-06 |
| `ParseSearchResponse_WithPhraseQuery_SetsExactPhraseMatchType` | FR-07 | AC-07 |
| `ParseSearchResponse_WithHighlight_UsesHighlightedText` | FR-07 | — |
| `ParseSearchResponse_WithFullPage_ReturnsNextToken` | FR-07 | — |
| `ParseSearchResponse_WithPartialPage_ReturnsNoNextToken` | FR-07 | — |
| `ParseSearchResponse_WithEmptyHits_ReturnsEmptyResults` | FR-07 | — |
| `ParseSearchResponse_WithMissingFields_UsesDefaults` | FR-07 | — |
| `ParseSearchResponse_WithMissingScore_DefaultsToZero` | FR-07 | — |
| `ParseSearchResponse_WithNoHitsProperty_ReturnsEmptyResults` | 健壮性 | — |
| `BuildIndexBody_IncludesBlockPipelineFields` | FR-06 | — |
| `BuildIndexBody_DoesNotContainLegacyFields` | FR-06 | — |
| `BuildIndexBody_RetainsSharedFieldsAndAnalyzers` | FR-06 | — |
| `ExactSearchAsync_DelegatesToSearchIndexService` | FR-07 | — |
| `ExactSearchAsync_PropagatesFilterToIndexService` | FR-07 | — |
| `ExactSearchAsync_PropagatesPageToken` | FR-07 | — |
| `ExactSearchAsync_WhenIndexServiceThrows_ReturnsEmptyResults` | FR-08 | AC-08 |
| `ExactSearchAsync_ReturnsEmptyResultsWhenNoMatch` | FR-07 | — |
| `BuildSearchBody_WithBlockTypeFilter_IncludesTerm` | FR-10 | AC-13 |
| `BuildSearchBody_WithPageNumberAndHasImage_WrapsAllInFilter` | FR-10, FR-12 | AC-16 |
| `BuildSearchBody_WithNoMinerUFilter_OutputEqualsV1` | — | AC-17（零回归） |
| `BuildSearchBody_WithKeywordAndMinerUFilter_SplitsMustAndFilter` | FR-10, FR-12 | — |
| `BuildSearchBody_WithAllMinerUFilters_IncludesAllTerms` | FR-10, FR-12 | AC-13~AC-16 |
| `ParseSearchResponse_WithBlockData_ReturnsBlockResultFields` | FR-11 | AC-18, AC-20 |
| `ParseSearchResponse_BboxAndScoreFields_MappedCorrectly` | FR-11 | AC-19 |
| `ParseSearchResponse_WithMissingMinerUFields_FallsBackToNull` | FR-11（健壮性） | — |
| `ParseSearchResponse_WithPartialBbox_StillConstructsArray` | FR-11（健壮性） | — |
| `ExactSearchAsync_PropagatesMinerUFilterToIndexService` | FR-10 | — |
| `ExactSearchAsync_WithMinerUFilterAndIndexServiceThrow_ReturnsEmpty` | 降级 | — |

### 第 2 代单元测试（追加到 `OpenSearchIndexServiceTests.cs` / `SearchDomainServiceTests.cs` / `DocumentParseBlockServiceTests.cs`，已实现）

在现有 V1 测试方法集中追加 minerU 断言（不新增独立测试方法类）。共 15 个测试方法，分布如下：

#### OpenSearchIndexServiceTests（10 个）

| 测试方法 | 覆盖 |
|---------|------|
| `BuildIndexBody_IncludesMinerUFields` | mapping 含 x0/y0/x1/y1/score/has_image/sub_type/text_level/text_format/caption/_meta（FR-12） |
| `BuildSearchBody_WithBlockTypeFilter_IncludesTerm` | minerU `blockType` 走 `filter: { block_type }`（FR-10） |
| `BuildSearchBody_WithPageNumberAndHasImage_WrapsAllInFilter` | 多 minerU 条件全走 `filter` 子句（FR-10, FR-12） |
| `BuildSearchBody_WithNoMinerUFilter_OutputEqualsV1` | 无 minerU filter 时 `BuildSearchBody` 输出与 V1 完全一致（零回归，AC-17） |
| `BuildSearchBody_WithKeywordAndMinerUFilter_SplitsMustAndFilter` | keyword 走 `must`、minerU 精确项走 `filter`（FR-10, FR-12） |
| `BuildSearchBody_WithAllMinerUFilters_IncludesAllTerms` | 8 个 minerU filter 全部命中（FR-10, FR-12） |
| `ParseSearchResponse_WithBlockData_ReturnsBlockResultFields` | `_source._meta.block_data` 映射到 `BlockData`（FR-11, AC-18, AC-20） |
| `ParseSearchResponse_BboxAndScoreFields_MappedCorrectly` | `x0/y0/x1/y1` 组合为 `Bbox`，`score` 字段回挂为 `MineruScore`（FR-11, AC-19） |
| `ParseSearchResponse_WithMissingMinerUFields_FallsBackToNull` | minerU 字段缺失时 `BlockData`/`Bbox`/`MineruScore` = null（健壮性） |
| `ParseSearchResponse_WithPartialBbox_StillConstructsArray` | 部分 bbox 分量存在时仍构造 float[4]（健壮性） |

#### SearchDomainServiceTests（2 个）

| 测试方法 | 覆盖 |
|---------|------|
| `ExactSearchAsync_PropagatesMinerUFilterToIndexService` | minerU filter 透传给 `ISearchIndexService`（FR-10） |
| `ExactSearchAsync_WithMinerUFilterAndIndexServiceThrow_ReturnsEmpty` | minerU filter + 索引异常 → 返回空结果（降级） |

#### DocumentParseBlockServiceTests（3 个）

| 测试方法 | 覆盖 |
|---------|------|
| `InsertBlocksFromContentListAsync_ExtractsMinerUFields_PipelineBbox` | pipeline 后端 bbox 0-1000 + score + sub_type + text_level + caption 抽取 |
| `InsertBlocksFromContentListAsync_NormalizesVlmBboxTo1000` | VLM 后端 bbox 0-1 百分比归一化到 0-1000 |
| `InsertBlocksFromContentListAsync_NoMinerUFields_DefaultsApplied` | 无 minerU 字段时默认值（null/empty/-1） |

## 集成测试（规划，未实现）

以下场景依赖真实 OpenSearch HTTP 与数据库，属于集成测试范畴，当前**未实现**：

| 场景 | 覆盖 AC |
|------|---------|
| MinerU 解析完成后 OpenSearch 有 blocks 数据 | AC-01 |
| 同一文档多次解析，OpenSearch 有多份 blocks | AC-02 |
| 删除 parse 后 OpenSearch 无该 parse 数据 | AC-03 |
| 删除文档后 OpenSearch 无该文档数据 | AC-04 |
| `PUT /{id}/metadata` 后 OpenSearch 元数据同步刷新 | AC-05 |
| 搜索能命中 blocks 数据 | AC-06 |
| OpenSearch 不可用时索引/删除 best-effort | NFR-03 |
| 第 2 代：解析完成后 OpenSearch 有 `x0/y0/x1/y1`/`score`/`has_image`/`_meta.block_data` | AC-13 |
| 第 2 代：`GET /admin/documents/search` 按 `blockType` / `hasImage` 命中并回挂 `blockData` | AC-13, AC-18, AC-16, AC-20 |
| 第 2 代：零回归——无 minerU filter 时行为完全等同 V1 | AC-17 |

## 前端验证（DS-19）

前端无单元测试框架（`package.json` 未配置 vitest/jest）。验证方式：

1. `cd frontend && npm install && npm run build`（vue-tsc 类型检查 + vite 构建）必须通过
2. 人工验证 `SearchPage.vue`：
   - 高级筛选抽屉展开/收起，8 个 minerU filter 输入控件可交互
   - "应用筛选" / "清空筛选" 按钮行为正确
   - 结果表格 minerU 列（块类型/subType/矿工 U 置信度/textFormat/Caption）正确显示
   - "展开详情"按钮行内展开 blockData 格式化 JSON + bbox + mineruScore 详情卡片
   - V1 零回归：不展开高级筛选、仅关键词搜索时，行为与改造前一致
3. 类型检查重点：`SearchResult` 新 optional 字段 + `searchTest` 新签名 TS 兼容性

## 未覆盖项

- **HTTP 端点参数校验**（query 空/过长 → 400、`pageSize` 截断）：无独立单元测试（不存在 `DocumentSearchEndpointsTests.cs`）。
- **Worker 集成**（解析完成后索引、LLM 元数据同步）：无单元测试（依赖 OpenSearch HTTP + 数据库，属集成测试）。
- **端点集成**（删除 parse/文件后清理索引）：无单元测试。
- **第 2 代首版未覆盖**：minerU `chars`/`position`/`layout_width`/`images`/`table_html`/`angle`/`formula_latex` 字段检索 —— 上述字段以 `block_data` jsonb 入库，管理界面通过 `GET /admin/document-files/{id}` 查看，延至 facet 需求明确后再进入 mapping（参见 [03-DESIGN.md §第 2 代演进](./03-DESIGN.md) 与 AC-22）。
