# DocumentSearch — Test Plan (TESTS)

Test tooling: `xUnit` + `Moq` + `FluentAssertions`.

Existing test files (actually present in code):
- `src/Tests/Doctheca.Tests/OpenSearch/OpenSearchQueryBuilderTests.cs`
- `src/Tests/Doctheca.Tests/OpenSearch/OpenSearchResponseParserTests.cs`
- `src/Tests/Doctheca.Tests/OpenSearchIndexServiceTests.cs`
- `src/Tests/Doctheca.Tests/SearchDomainServiceTests.cs`

> Note: `DocumentSearchEndpointsTests.cs` does not exist (mislisted in old docs). HTTP endpoints have no dedicated unit tests.

## How to Run

```bash
dotnet test src/Tests/Doctheca.Tests \
  --configuration Release \
  --filter "FullyQualifiedName~OpenSearchQueryBuilderTests|FullyQualifiedName~OpenSearchResponseParserTests|FullyQualifiedName~OpenSearchIndexServiceTests|FullyQualifiedName~SearchDomainServiceTests"
```

## Unit Test Inventory (actual method names)

### OpenSearchQueryBuilderTests (`OpenSearch/OpenSearchQueryBuilderTests.cs`)

Pure logic tests (`OpenSearchQueryBuilder.BuildSearchBody`), no OpenSearch HTTP dependency.

| Test method | Coverage |
|---------|------|
| `BuildSearchBody_WithPhraseQuery_UsesMatchPhraseOnTextExact` | Phrase queries use `match_phrase` + `text.exact` (FR-07) |
| `BuildSearchBody_WithStemmedQuery_UsesMatchOnText` | Word queries use `match` + `text` (FR-07) |
| `BuildSearchBody_WithFullFilter_WrapsInBoolWithAllClauses` | Full-field filter wraps in `bool.must + filter`, including `file_name` (FR-07, FR-09) |
| `BuildSearchBody_WithNullFilter_DoesNotWrapInBool` | A null filter is not wrapped in `bool` (FR-07) |
| `BuildSearchBody_WithPartialFilter_OnlyIncludesNonEmptyFields` | Partially empty fields do not participate in filtering (FR-07) |
| `BuildSearchBody_WithValidPageToken_IncludesSearchAfter` | A valid pageToken decodes to `search_after` (FR-07, FR-09) |
| `BuildSearchBody_WithInvalidPageToken_DoesNotIncludeSearchAfter` | An invalid Base64 pageToken does not produce `search_after` (error handling) |
| `BuildSearchBody_WithNullOrEmptyPageToken_DoesNotIncludeSearchAfter` | A null/empty pageToken does not produce `search_after` (FR-07) |
| `BuildSearchBody_AlwaysIncludesSizeSortAndHighlight` | Always includes `size`/`sort`/`highlight` (FR-07) |
| `BuildSearchBody_SortUsesBlockIdNotLegacyFields` | Sorting uses `block_id`, no legacy fields (FR-07) |

### OpenSearchResponseParserTests (`OpenSearch/OpenSearchResponseParserTests.cs`)

Pure logic tests (`OpenSearchResponseParser.ParseSearchResponse`), no OpenSearch HTTP dependency.

| Test method | Coverage |
|---------|------|
| `ParseSearchResponse_WithValidJson_ReturnsResultsWithBlockFields` | Normal parsing: field mapping, `MatchType=Stemmed`, offset=0 (FR-07, AC-06) |
| `ParseSearchResponse_WithPhraseQuery_SetsExactPhraseMatchType` | Phrase queries set `MatchType=ExactPhrase` (FR-07, AC-07) |
| `ParseSearchResponse_WithHighlight_UsesHighlightedText` | Highlight takes priority over `_source.text` (FR-07) |
| `ParseSearchResponse_WithFullPage_ReturnsNextToken` | `nextToken` generated on a full page (FR-07) |
| `ParseSearchResponse_WithPartialPage_ReturnsNoNextToken` | No `nextToken` on a partial page (FR-07) |
| `ParseSearchResponse_WithEmptyHits_ReturnsEmptyResults` | Empty hits return empty results (FR-07) |
| `ParseSearchResponse_WithMissingFields_UsesDefaults` | Missing `_source` fields use defaults (FR-07) |
| `ParseSearchResponse_WithMissingScore_DefaultsToZero` | Defaults to 0 when `_score` is missing (FR-07) |
| `ParseSearchResponse_WithNoHitsProperty_ReturnsEmptyResults` | Returns empty results when the `hits` property is absent (robustness) |

### OpenSearchIndexServiceTests (`OpenSearchIndexServiceTests.cs`)

Pure logic tests (`OpenSearchIndexService.BuildIndexBody` passes through to `OpenSearchIndexManager`), no OpenSearch HTTP dependency.

| Test method | Coverage |
|---------|------|
| `BuildIndexBody_IncludesBlockPipelineFields` | Mapping contains block fields with correct types (FR-06) |
| `BuildIndexBody_DoesNotContainLegacyFields` | Mapping contains no legacy fields (FR-06) |
| `BuildIndexBody_RetainsSharedFieldsAndAnalyzers` | Retains shared fields and analyzers (`english_custom`/`english_phrase`/sub-field `exact`) (FR-06) |

### SearchDomainServiceTests (`SearchDomainServiceTests.cs`)

Mocks `ISearchIndexService` to verify domain service delegation and degradation.

| Test method | Coverage |
|---------|------|
| `ExactSearchAsync_DelegatesToSearchIndexService` | Delegates to `ISearchIndexService.ExactSearchAsync` (FR-07) |
| `ExactSearchAsync_PropagatesFilterToIndexService` | Filter passed through to the index service (FR-07) |
| `ExactSearchAsync_PropagatesPageToken` | pageToken passed through to the index service (FR-07) |
| `ExactSearchAsync_WhenIndexServiceThrows_ReturnsEmptyResults` | Returns empty results + LogWarning when the index service throws (FR-08, AC-08) |
| `ExactSearchAsync_ReturnsEmptyResultsWhenNoMatch` | Returns empty results when no match (FR-07) |

## FR/AC Mapping

| Test method | Verifies FR | Verifies AC |
|---------|---------|---------|
| `BuildSearchBody_WithPhraseQuery_UsesMatchPhraseOnTextExact` | FR-07 | AC-07 |
| `BuildSearchBody_WithStemmedQuery_UsesMatchOnText` | FR-07 | AC-07 |
| `BuildSearchBody_WithFullFilter_WrapsInBoolWithAllClauses` | FR-07 | — |
| `BuildSearchBody_WithNullFilter_DoesNotWrapInBool` | FR-07 | — |
| `BuildSearchBody_WithPartialFilter_OnlyIncludesNonEmptyFields` | FR-07 | — |
| `BuildSearchBody_WithValidPageToken_IncludesSearchAfter` | FR-07 | — |
| `BuildSearchBody_WithInvalidPageToken_DoesNotIncludeSearchAfter` | Error handling | — |
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
| `ParseSearchResponse_WithNoHitsProperty_ReturnsEmptyResults` | Robustness | — |
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
| `BuildSearchBody_WithNoMinerUFilter_OutputEqualsV1` | — | AC-17 (zero regression) |
| `BuildSearchBody_WithKeywordAndMinerUFilter_SplitsMustAndFilter` | FR-10, FR-12 | — |
| `BuildSearchBody_WithAllMinerUFilters_IncludesAllTerms` | FR-10, FR-12 | AC-13~AC-16 |
| `ParseSearchResponse_WithBlockData_ReturnsBlockResultFields` | FR-11 | AC-18, AC-20 |
| `ParseSearchResponse_BboxAndScoreFields_MappedCorrectly` | FR-11 | AC-19 |
| `ParseSearchResponse_WithMissingMinerUFields_FallsBackToNull` | FR-11 (robustness) | — |
| `ParseSearchResponse_WithPartialBbox_StillConstructsArray` | FR-11 (robustness) | — |
| `ExactSearchAsync_PropagatesMinerUFilterToIndexService` | FR-10 | — |
| `ExactSearchAsync_WithMinerUFilterAndIndexServiceThrow_ReturnsEmpty` | Degradation | — |

### Generation 2 Unit Tests (added to `OpenSearch/OpenSearchQueryBuilderTests.cs`, `OpenSearch/OpenSearchResponseParserTests.cs`, `OpenSearchIndexServiceTests.cs` / `SearchDomainServiceTests.cs` / `DocumentParseBlockServiceTests.cs`, implemented)

minerU assertions added within the existing V1 test method sets (no standalone test classes). 15 test methods in total, distributed as follows:

#### OpenSearchQueryBuilderTests (5)

| Test method | Coverage |
|---------|------|
| `BuildSearchBody_WithBlockTypeFilter_IncludesTerm` | minerU `blockType` goes to `filter: { block_type }` (FR-10) |
| `BuildSearchBody_WithPageNumberAndHasImage_WrapsAllInFilter` | Multiple minerU conditions all go to `filter` clauses (FR-10, FR-12) |
| `BuildSearchBody_WithNoMinerUFilter_OutputEqualsV1` | With no minerU filters, `BuildSearchBody` output is identical to V1 (zero regression, AC-17) |
| `BuildSearchBody_WithKeywordAndMinerUFilter_SplitsMustAndFilter` | Keyword goes to `must`, minerU exact terms go to `filter` (FR-10, FR-12) |
| `BuildSearchBody_WithAllMinerUFilters_IncludesAllTerms` | All 8 minerU filters hit (FR-10, FR-12) |

#### OpenSearchResponseParserTests (4)

| Test method | Coverage |
|---------|------|
| `ParseSearchResponse_WithBlockData_ReturnsBlockResultFields` | `_source._meta.block_data` maps to `BlockData` (FR-11, AC-18, AC-20) |
| `ParseSearchResponse_BboxAndScoreFields_MappedCorrectly` | `x0/y0/x1/y1` combine into `Bbox`; the `score` field is attached as `MineruScore` (FR-11, AC-19) |
| `ParseSearchResponse_WithMissingMinerUFields_FallsBackToNull` | `BlockData`/`Bbox`/`MineruScore` = null when minerU fields are missing (robustness) |
| `ParseSearchResponse_WithPartialBbox_StillConstructsArray` | Constructs float[4] even when only some bbox components exist (robustness) |

#### OpenSearchIndexServiceTests (1)

| Test method | Coverage |
|---------|------|
| `BuildIndexBody_IncludesMinerUFields` | Mapping contains x0/y0/x1/y1/score/has_image/sub_type/text_level/text_format/caption/_meta (FR-12) |

#### SearchDomainServiceTests (2)

| Test method | Coverage |
|---------|------|
| `ExactSearchAsync_PropagatesMinerUFilterToIndexService` | minerU filter passed through to `ISearchIndexService` (FR-10) |
| `ExactSearchAsync_WithMinerUFilterAndIndexServiceThrow_ReturnsEmpty` | minerU filter + index exception → returns empty results (degradation) |

#### DocumentParseBlockServiceTests (3)

| Test method | Coverage |
|---------|------|
| `InsertBlocksFromContentListAsync_ExtractsMinerUFields_PipelineBbox` | pipeline backend bbox 0-1000 + score + sub_type + text_level + caption extraction |
| `InsertBlocksFromContentListAsync_NormalizesVlmBboxTo1000` | VLM backend bbox 0-1 percentage normalized to 0-1000 |
| `InsertBlocksFromContentListAsync_NoMinerUFields_DefaultsApplied` | Defaults when minerU fields are absent (null/empty/-1) |

## Integration Tests (planned, not implemented)

The following scenarios depend on real OpenSearch HTTP and a database, fall under integration testing, and are currently **not implemented**:

| Scenario | Covers AC |
|------|---------|
| After parse completion, OpenSearch has block data | AC-01 |
| Same document parsed multiple times, OpenSearch has multiple block sets | AC-02 |
| After deleting a parse, OpenSearch has no data for that parse | AC-03 |
| After deleting a document, OpenSearch has no data for that document | AC-04 |
| After `PUT /{id}/metadata`, OpenSearch metadata refreshed in sync | AC-05 |
| Search hits block data | AC-06 |
| Indexing/deletion best-effort when OpenSearch is unavailable | NFR-03 |
| Generation 2: after parse completion, OpenSearch has `x0/y0/x1/y1`/`score`/`has_image`/`_meta.block_data` | AC-13 |
| Generation 2: `GET /admin/documents/search` hits by `blockType` / `hasImage` and attaches `blockData` | AC-13, AC-18, AC-16, AC-20 |
| Generation 2: zero regression — behavior exactly equivalent to V1 when no minerU filters | AC-17 |

## Frontend Verification (DS-19)

The frontend has no unit test framework (`package.json` configures neither vitest nor jest). Verification approach:

1. `cd frontend && npm install && npm run build` (vue-tsc type check + vite build) must pass
2. Manual verification of `SearchPage.vue`:
   - Advanced filters drawer expands/collapses; the 8 minerU filter input controls are interactive
   - "Apply filters" / "Clear filters" buttons behave correctly
   - Result table minerU columns (block type/subType/minerU confidence/textFormat/Caption) display correctly
   - The "Expand details" button expands inline blockData formatted JSON + bbox + mineruScore detail cards
   - V1 zero regression: with the advanced filters drawer closed and keyword-only search, behavior matches pre-change
3. Type-check focus: new optional `SearchResult` fields + TS compatibility of the new `searchTest` signature

## Uncovered Items

- **HTTP endpoint parameter validation** (empty/too-long query → 400, `pageSize` truncation): no dedicated unit tests (`DocumentSearchEndpointsTests.cs` does not exist).
- **Worker integration** (indexing after parse completion, LLM metadata sync): no unit tests (depends on OpenSearch HTTP + database; belongs to integration testing).
- **Endpoint integration** (index cleanup after deleting parses/files): no unit tests.
- **Not covered in the first Generation 2 release**: retrieval of minerU `chars`/`position`/`layout_width`/`images`/`table_html`/`angle`/`formula_latex` fields — these fields are persisted as `block_data` jsonb and viewed in the admin UI via `GET /admin/document-files/{id}`, deferred into the mapping until facet requirements are clear (see [03-DESIGN.md §Generation 2 Evolution](./03-DESIGN.md) and AC-22).
