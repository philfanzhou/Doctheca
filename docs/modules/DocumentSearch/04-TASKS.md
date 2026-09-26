# DocumentSearch — Task List (TASKS)

> **This module's code is fully implemented (Generation 1 + Generation 2 backend).** The Generation 2 frontend (DS-19) is a separate project; its status remains planned.
> The following are code review and automated verification records.

## Task Breakdown

| ID | Task | Status | Verification file |
|----|------|------|---------|
| DS-01 | Index mapping definition (`BuildIndexBody`): confirm block fields, analyzers, absence of legacy fields | completed | `OpenSearchIndexService.cs` |
| DS-02 | Index writes (`IndexParseBlocksAsync`): bulk construction, empty-text skipping, idempotent `_id` | completed | `OpenSearchIndexService.cs` |
| DS-03 | Index deletion (`DeleteParseIndexAsync` / `DeleteDocumentFileIndexAsync`): `delete_by_query` | completed | `OpenSearchIndexService.cs` |
| DS-04 | Metadata sync (`UpdateDocumentFileMetadataAsync`): `update_by_query` + script | completed | `OpenSearchIndexService.cs` |
| DS-05 | Search query building (`BuildSearchBody`): phrase/match, filter, search_after, sort, highlight | completed | `OpenSearchIndexService.cs` |
| DS-06 | Search response parsing (`ParseSearchResponse`): field mapping, nextToken, highlight priority | completed | `OpenSearchIndexService.cs` |
| DS-07 | Search domain service (`SearchDomainService`): delegation + exception degradation | completed | `SearchDomainService.cs` |
| DS-08 | HTTP endpoint (`DocumentSearchEndpoints`): parameter validation, filter construction, response format | completed | `DocumentSearchEndpoints.cs` |
| DS-09 | Worker integration: indexing after parse completion (best-effort), LLM metadata sync | completed | `StructaDocParseWorker.cs` |
| DS-10 | Endpoint integration: index cleanup after deleting parses/files, sync after metadata updates | completed | `DocumentParseEndpoints.cs` / `DocumentFileEndpoints.cs` |
| DS-11 | Startup initialization: `EnsureIndexAsync` (best-effort) | completed | `Program.cs` |
| DS-12 | Unit tests: `OpenSearchIndexServiceTests` + `SearchDomainServiceTests` | completed | Test files |

### Generation 2 Tasks (evolving V1, block structured retrieval)

| ID | Task | Status | Verification file |
|----|------|------|---------|
| DS-13 | Extend `OpenSearchIndexService.IndexParseBlocksAsync`: append minerU dimension fields in the same bulk (`x0`/`y0`/`x1`/`y1`/`score`/`has_image`/`sub_type`/`text_level`/`text_format`/`caption`/`_meta.block_data`); extend `BuildIndexBody` with minerU dimension mappings | completed | `OpenSearchIndexService.cs` |
| DS-14 | Extend `StructaDocParseWorker.IndexBlocksToSearchAsync`: call the extended `IndexParseBlocksAsync` (same best-effort entry point). **Implementation note**: the Worker itself needs no changes — minerU fields are extracted by `DocumentParseBlockService.ParseBlock` and written to `DocumentParseBlockEntity`, persisted via `DocumentParseBlockRepository`, then read by `IndexParseBlocksAsync` via `GetByParseIdAsync` and indexed directly; the field flow propagates automatically. | completed | `StructaDocParseWorker.cs` (no changes) |
| DS-15 | Extend `SearchResultModel` (add optional `BlockData`/`Bbox`/`MineruScore`/`SubType`/`TextLevel`/`TextFormat`/`Caption`) + `SearchFilterModel` (add `BlockType`/`BlockSubType`/`PageNumber`/`TextLevel`/`TextFormat`/`ParseId`/`DocumentFileId`/`HasImage`). **Deviation note**: SPEC §13.1.1 originally proposed `float? Score`, but V1 already has `public double Score` (OpenSearch `_score`); C# does not allow same-named fields, so the Generation 2 field is named `MineruScore` (consistent with `block.MineruScore` in §13.9.1); the HTTP response JSON key is `mineruScore`. See the deviation note in 02-SPEC.md §13.1.1. | completed | `SearchResultModel.cs` / `SearchFilterModel.cs` |
| DS-16 | Extend `OpenSearchIndexService.ExactSearchAsync` (`BuildSearchBody` gains the minerU filter branch + `ParseSearchResponse` gains the minerU attachment branch) | completed | `OpenSearchIndexService.cs` |
| DS-17 | Extend `SearchDomainService.ExactSearchAsync`: pass through minerU filters + parse attachment fields (extended within the same method, reusing the V1 degradation pattern). **Implementation note**: `SearchDomainService` is a thin wrapper; filter pass-through happens automatically via `SearchFilterModel`, so the method body needs no changes; the exception degradation path is already covered by the UT `ExactSearchAsync_WithMinerUFilterAndIndexServiceThrow_ReturnsEmpty`. | completed | `SearchDomainService.cs` (no changes) |
| DS-18 | Extend `DocumentSearchEndpoints.cs`: the same `GET /admin/documents/search` gains optional minerU inputs (`blockType`/`blockSubType`/`pageNumber`/`textLevel`/`textFormat`/`parseId`/`documentFileId`/`hasImage`); V1 validation retained; zero regression when parameters are null. The response gains the optional `blockData`/`bbox`/`mineruScore`/`subType`/`textLevel`/`textFormat`/`caption` fields. | completed | `DocumentSearchEndpoints.cs` |
| DS-19 | doctheca built-in frontend extension of `SearchPage.vue`: add an "Advanced filters" drawer (blockType/blockSubType/pageNumber/textLevel/textFormat/parseId/documentFileId/hasImage) + result table minerU columns (block type/subType/minerU confidence/textFormat/Caption) + row expansion with blockData/bbox/mineruScore detail cards; `docApi.ts` `SearchResult` gains 7 optional fields + the `searchTest` signature is extended with 8 optional parameters (null values not passed through, zero regression); `frontend-spec.md` §4.4 updated in sync. Verification: `npm run build` (vue-tsc + vite build) passes. | completed | `SearchPage.vue` / `docApi.ts` / `frontend-spec.md` |
| DS-20 | Unit test additions: add minerU filter construction / `blockData` attachment / default fallback / bbox normalization / zero-regression assertions to the existing `OpenSearchIndexServiceTests` / `SearchDomainServiceTests` / `DocumentParseBlockServiceTests` (no new test classes). 15 new test methods, all passing (169/169). | completed | Test files |

## Generation 2 Implementation Notes

### Database schema changes (DatabaseInitializer raw SQL)
9 new columns added to the `document_parse_blocks` table (via `ALTER TABLE ADD COLUMN IF NOT EXISTS`):
`sub_type` / `text_level` / `text_format` / `bbox_x0` / `bbox_y0` / `bbox_x1` / `bbox_y1` / `score` / `caption`

> The column name `score` follows the rule at 06-CONVENTIONS line 103 that "genuinely new fields do not take the mineru_ prefix"; the C# property name `MineruScore` avoids confusion with the V1 concept.

### Verification results
- `dotnet build src/Doctheca.sln --configuration Release`: **success** (0 errors, 3 unrelated nullable warnings)
- `dotnet test ... --configuration Release`: **169/169 passing** (including the AC-17 zero-regression `BuildSearchBody_WithNoMinerUFilter_OutputEqualsV1`)

## Command Quick Reference

```bash
# Build
dotnet build Doctheca.sln --configuration Release

# Run tests related to this module
dotnet test src/Tests/Doctheca.Tests \
  --configuration Release \
  --filter "FullyQualifiedName~OpenSearchIndexServiceTests|FullyQualifiedName~SearchDomainServiceTests"

# All tests
dotnet test src/Tests/Doctheca.Tests --configuration Release
```

## Dependency Graph

```
DocumentSearchEndpoints.Search
  └── SearchDomainService.ExactSearchAsync
        └── OpenSearchIndexService.ExactSearchAsync
              ├── BuildSearchBody (internal static)
              ├── OpenSearchLowLevelClient.SearchAsync
              └── ParseSearchResponse (internal static)

StructaDocParseResultSync.SyncAsync
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

DocumentSearchEndpoints.Search (Generation 2 extension)
  └── SearchDomainService.ExactSearchAsync (minerU filter branch + attachment parsing within the same method)
        └── OpenSearchIndexService.ExactSearchAsync (minerU filter + attachment within the same method)
              ├── BuildSearchBody (Generation 2 adds minerU filter clauses; output equals V1 when no minerU filters)
              ├── OpenSearchLowLevelClient.SearchAsync
              └── ParseSearchResponse (Generation 2 adds minerU attachment field mapping)

StructaDocParseWorker.IndexBlocksToSearchAsync
  └── OpenSearchIndexService.IndexParseBlocksAsync (Generation 2 appends minerU dimension fields in the same bulk)
        ├── IDocumentParseBlockRepository.GetByParseIdAsync
        └── OpenSearchLowLevelClient.BulkAsync   (V1's 6 method signatures unchanged)

Program.cs (startup)
  └── OpenSearchIndexService.EnsureIndexAsync (best-effort)
        └── BuildIndexBody (still generated by the same method after Generation 2 adds minerU dimension mappings)
```
