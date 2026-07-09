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

## 未覆盖项

- **HTTP 端点参数校验**（query 空/过长 → 400、`pageSize` 截断）：无独立单元测试（不存在 `DocumentSearchEndpointsTests.cs`）。
- **Worker 集成**（解析完成后索引、LLM 元数据同步）：无单元测试（依赖 OpenSearch HTTP + 数据库，属集成测试）。
- **端点集成**（删除 parse/文件后清理索引）：无单元测试。
