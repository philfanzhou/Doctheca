# DocumentSearch — Design Notes (DESIGN)

## Design Decisions

### Why Indexing and Search Live in the Same Service

Index writes and exact search share the same OpenSearch mapping (`BuildIndexBody`) and are tightly coupled:
- Queried fields (`text` / `text.exact` / `file_name` / `subject` / `grade` / `year`) must strictly match the indexed fields.
- The field names read by `ParseSearchResponse` (`block_id` / `file_name` / `page_number`) share their source with the field names written by `IndexParseBlocksAsync`.
- Splitting into two services would require cross-service synchronization when the mapping changes, increasing the risk of inconsistency.

They are therefore merged into the same module document (this module), with mapping and query logic maintained in one place.

### best-effort Indexing Strategy

All OpenSearch write operations (index/delete/metadata sync) are best-effort:
- Failures only log a Warning; no exception is thrown to the outer layer.
- **Rationale**: search is an auxiliary capability and must not block or break the core parsing pipeline. After parse results are persisted to the database, even if OpenSearch indexing fails no data is lost; only search is temporarily unavailable.
- The Worker and endpoint layers uniformly wrap calls in `try/catch + LogWarning`.

### Search Degradation Strategy

`SearchDomainService` acts as a thin wrapper layer over `ISearchIndexService` and handles exceptions uniformly:
- Catches **any** exception (not limited to specific types), logs a Warning, and returns empty results.
- **Rationale**: HTTP search requests should not return 500 because of an OpenSearch failure; callers (frontend/downstream) receiving empty results can render "no results" normally.
- The degradation logic lives in the domain layer (`SearchDomainService`), not the HTTP endpoint layer, ensuring reusability.

### Query Building Testability

`OpenSearchIndexService`'s `OpenSearchLowLevelClient` is created via `new` in the constructor, making it hard to inject mocks. The pure logic is therefore extracted into `internal static` methods:
- `BuildIndexBody()` — index mapping/settings
- `BuildSearchBody(query, phrase, filter, pageSize, pageToken, logger)` — search request body
- `ParseSearchResponse(responseJson, phrase, pageSize)` — response parsing

These methods have no external dependencies; unit tests can call them directly to verify field/logic correctness (exposed to the test assembly via `InternalsVisibleTo`).

## Generation 2 Evolution: Block-Level Retrieval over minerU v1 JSON

> **Evolution approach: merge into the V1 index / V1 endpoint / V1 frontend** — no parallel endpoint, no standalone `BlockXxx` model classes, no separate parallel `BlockSearchPage.vue` page. Rationale (from the @user design review): the admin backend's "block-level retrieval" shares the same OpenSearch index, the same `document_parse_blocks` table, the same `StructaDocParseWorker` entry point, the same `SearchDomainService` degradation chain, and the same doctheca built-in frontend as the existing "exact keyword retrieval" — opening a parallel path would mean maintaining two retrieval systems.

### minerU v1 Block Field Sources and Layering

> The real schema of minerU (formerly magic-pdf / PDF-Extract-Kit) v1 `content_list.json` cannot be verified online in this environment (github.com / pypi.org / opendatalab.github.io are all blocked by the gateway; WebSearch returns generic templates), but **the project's existing code read paths + the MinerU public enumeration consensus** are sufficient to implement the evolution:

**Dimension 1 — existing V1 fields, reused directly (same source, no alias double-write)**:
- `block_type` ← minerU `type` (`DocumentParseBlockService.ParseBlock:95-100`)
- `page_number` (= `block.PageId`) ← minerU `page_id` (`DocumentParseBlockService.cs:104-107`)
- `text` (= `block.TextContent`) ← minerU `text|content|body` priority-based extraction (`DocumentParseBlockService.cs:151`)

These 3 dimensions **add no new mapping fields in this evolution**; they are only **exposed as filter parameters** on the V1 endpoint (the V1 mapping already indexes `block_type` / `page_number` / `text`, but the endpoint `GET /admin/documents/search` has not yet opened filtering by `blockType` / `pageNumber`).

**Dimension 2 — minerU official authoritative schema (confirmed by the minerU official documentation)**:
Authoritative source for the minerU output structure: the minerU official documentation `docs/zh/reference/output_files.md` (two versions for the `pipeline` backend + the `VLM` backend, with slight structural differences). read via `curl https://raw.githubusercontent.com/opend_lab/MinerU/master/docs/zh/reference/output_files.md` (28KB, 867 lines).

### minerU `content_list.json` Block Type Authoritative Enumeration

#### pipeline backend (verified: `DocumentParseBlockService.ParseBlock` consumes this format)

```
Common fields: type, bbox [x0,y0,x1,y1] (0-1000 normalized), page_idx (0-based)

Top-level block type:            Subtypes (distinguished via secondary block/sub_type)
├── text                     text / title / index / list / interline_equation
├── image                    image_body, image_caption, image_footnote
├── table                    table_body, table_caption, table_footnote, table_body(<html>)
├── chart                    chart_body, chart_caption, chart_footnote
└── discarded_blocks         header, footer, page_number, aside_text, page_footnote

Secondary text span fields:    type (text/image/table/chart/inline_equation/interline_equation), bbox, content | image_path
Derived fields (first V2 index release):     text_level (0=body,1=h1,2=h2..., absent for non-heading), sub_type, caption
```

#### VLM backend (official documentation confirms: overall flatter structure)

```
Top-level fields: type, bbox [x0,y0,x1,y1] (0-1 percentage ⚠️), content, angle (0/90/180/270), score, text_format (latex/markdown/none)

type enumeration (complete): text, title, equation, image, image_caption, image_footnote,
                   table, table_caption, table_footnote, chart, chart_caption, chart_footnote,
                   code, code_caption, algorithm, phonetic, ref_text, list (sub_type: text/ref_text),
                   header, footer, page_number, aside_text, page_footnote

New fields vs pipeline: text_level, text_format, sub_type (code distinguishes code/algorithm; list distinguishes text/ref_text), list_items
```

⚠️ **Key difference**: the VLM backend `bbox` is a **0-1 percentage**, while the pipeline backend `bbox` is **0-1000 normalized**. Before writing to OpenSearch, `DocumentParseBlockService.ParseBlock` normalizes uniformly to 0-1000 (the pipeline convention) to avoid coordinate ambiguity in frontend/mixed-index scenarios.

`[Authoritative] The minerU official schema above comes from docs/zh/reference/output_files.md; specific enumerations follow the latest minerU version of that document.`

**Dimension 3 — fields that genuinely need to be *newly* mapped into OpenSearch** (the V1 index currently has no corresponding columns):

| minerU field | Type | Purpose | Enters mapping? |
|------------|------|------|--------------|
| `bbox` `[x0,y0,x1,y1]` | float[4] | Page coordinates (top-left origin) → supports filtering by visual region | Yes (`float[]` + separate `x0/y0/x1/y1` to facilitate range queries) |
| `score` | float | MinerU confidence → supports "high-confidence block" filtering (investigating parse quality issues) | Yes (float) |
| `image_path` / `img_path` | string | Valid only for image/figure blocks → boolean index of whether an image is carried | Yes (derived `has_image` bool; the raw `image_path` goes into `block_data`) |
| `sub_type` | string | Official minerU field distinguishing caption/body/footnote secondary classifications (e.g., `table_caption`, `code`, `algorithm`, `text`, `ref_text`) → supports filters like "all captions", "all code blocks" | Yes (keyword) |
| `text_level` | int | Official minerU field, heading level (`0` = body text, `1` = h1, `2` = h2...; absent for non-heading text) → supports "all h1 heading blocks" filtering | Yes (integer; non-heading text indexed as `-1`) |
| `text_format` | string | minerU VLM-backend-specific field (`latex` / `markdown` / `none`) → supports "interline equation block" filtering | Yes (keyword; indexed as an empty string when the pipeline backend lacks this field) |
| `caption` | text | Derived field: concatenates caption texts such as `image_caption` / `table_caption` / `chart_caption` / `code_caption` into searchable text → lets keyword search hit figure/table caption content | Yes (text, english_custom analyzer) |
| `block_data` full raw text | object | Admin UI "view raw minerU JSON" details | Yes (nested `_meta.block_data`, `enabled:false`, stored but not indexed) |

> Other minerU fields (`chars`, `position`, `layout_width`, `images`, `table_html`, `angle`, `block_tags`, `content_tags`, `list_items`, `code_body`, `code_language`, `table_body`, etc.) **do not enter the mapping** in the first release — they are already persisted in `document_parse_blocks.block_data` (jsonb), shown in the admin UI via `GET /admin/document-files/{id}` when needed, or deferred until facet requirements are clear.

### Concrete Changes for Evolving V1 (merged index + extended endpoint)

**Option P (original parallel V2, rejected)**: add standalone `BlockSearchEndpoints` + `BlockResultModel` + `BlockSearchPage.vue` classes/endpoints/pages → maintaining two retrieval systems.

**Option Q (evolve V1, selected)**:

| Layer | Change | Reuse |
|----|------|------|
| OpenSearch index mapping | V1 `BuildIndexBody` gains `bbox`/`x0 y0 x1 y1`/`score`/`has_image`/`_meta.block_data` | Same index, same `_id` |
| Index-writing Worker | `StructaDocParseWorker.IndexBlocksToSearchAsync` appends the new fields in the same bulk | Same best-effort entry point |
| Domain service | `SearchDomainService.ExactSearchAsync` extended + new parameters; **no new methods**, only filter/return-field branches added within the same method | The extended signature delegated to `ISearchIndexService` |
| Endpoint | `DocumentSearchEndpoints.Search` **extended**: new optional inputs `blockType`/`pageNumber`/`hasImage`; the returned `SearchResultModel` gains `BlockData`/`Bbox`/`Score` fields (new fields optional, compatible with old frontends) | Same `GET /admin/documents/search` |
| Frontend | `SearchPage.vue` gains an "Advanced filters" drawer (minerU field filtering) + result-row expansion showing `blockData` | Same page, not side by side |

**Decision**: Option Q selected (evolve V1).

Rationale:
- Indexing/writes/degradation/pagination are fully reused; maintenance effort is near minimal.
- The result contract is backward compatible: the new fields `BlockData`/`Bbox`/`Score` are optional in `SearchResultModel`; frontends that do not send/display them (including old Ruoyu.Admin references, if any) are unaffected.
- The admin UI entry point remains in the doctheca built-in frontend; the Ruoyu.Admin boundary is not expanded.
- minerU `type` shares its source with `block_type`, `page_id` with `page_number`, `text|content|body` with `text_content` → Dimension 1 has zero new mapping cost.

### `block_data` Attachment Strategy (same as Option B of the parallel V2, retained after simplification)

| Option | Write amplification | Query latency | Trade-off |
|------|---------|---------|------|
| A. Flat top-level string field | Medium | Low | Rejected |
| **B. Nested `_meta.block_data` (`enabled:false`)** | **Low (stored, not indexed)** | **Low (attached from `_source`)** | **Selected** |
| C. Separate index | High | Medium | Rejected |
| D. Re-query the DB on every request | 0 writes | High (second round trip) | Rejected |

### Conventions for Exposing the minerU type Enumeration via the Endpoint

The V1 endpoint's new `blockType` parameter accepts a **string** (free text); the server uses `term` exact matching on the `block_type` index field. Enum validation is not enforced — parser-side upgrades will introduce new types (existing records use MinerU types, new records use StructaDoc canonical types), and a validation whitelist would become a burden. The frontend dropdown is based on **aggregation of historical data** (ops can see the current value domain in the "block type distribution" sidebar).

`[Note] If the minerU type enumeration changes frequently later, a static candidate list can be maintained in the frontend dropdown; the backend still passes strings through.`

## Directory and File Structure

```

├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── SearchResultModel.cs               # Search result model (Generation 2 evolution adds BlockData/Bbox/Score)
│   │   │   ├── SearchFilterModel.cs               # Search filter model (Generation 2 evolution adds BlockType/PageNumber/HasImage)
│   │   │   ├── SearchMatchType.cs                 # Match type constants (unchanged)
│   │   │   └── OpenSearchOptions.cs               # OpenSearch configuration section (unchanged)
│   │   ├── Services/
│   │   │   ├── ISearchDomainService.cs            # Search domain service interface (ExactSearchAsync extended signature)
│   │   │   └── SearchDomainService.cs             # Search domain service implementation (filters extended within the same ExactSearchAsync)
│   │   └── Repositories/
│   │       ├── ISearchIndexService.cs             # Search index interface (adds minerU field indexing)
│   │       └── IDocumentParseBlockRepository.cs   # Block repository interface (unchanged, no new GetPagedAsync method)
│   ├── Service/
│   │   ├── Endpoints/
│   │   │   └── DocumentSearchEndpoints.cs         # GET /admin/documents/search (Generation 2 evolution extends inputs and response)
│   │   ├── OpenSearch/
│   │   │   ├── OpenSearchIndexManager.cs          # Index lifecycle management (create/delete/version check)
│   │   │   ├── OpenSearchQueryBuilder.cs          # Search request body construction (BuildSearchBody)
│   │   │   ├── OpenSearchResponseParser.cs        # Search response parsing (ParseSearchResponse)
│   │   │   └── OpenSearchJsonHelper.cs            # Safe OpenSearch JSON field reading helpers
│   │   ├── OpenSearchIndexService.cs              # OpenSearch index service facade (composes the classes above, implements ISearchIndexService)
│   │   └── StructaDocParseWorker.cs               # Indexing after parse completion (best-effort, adds minerU fields)
│   └── Host/
│       ├── frontend/                              # doctheca built-in frontend
│       │   ├── src/pages/
│       │   │   └── SearchPage.vue                 # Search page (Generation 2 evolution adds the "Advanced filters" drawer + blockData expansion)
│       ├── appsettings.json                       # OpenSearch configuration (unchanged)
│       └── Program.cs                             # DI registration (unchanged)
└── src/Tests/Doctheca.Tests/
    ├── OpenSearchIndexServiceTests.cs             # Adds minerU field mapping/query/attachment test cases
    └── SearchDomainServiceTests.cs                # Adds minerU filter pass-through + attachment degradation test cases
```

## Key Interface Signatures

### ISearchIndexService

See [02-SPEC.md §1.1](./02-SPEC.md#11-isearchindexservice).

### Injected Dependencies

```csharp
// OpenSearchIndexService constructor
public sealed class OpenSearchIndexService : ISearchIndexService
{
    public OpenSearchIndexService(
        IOptions<OpenSearchOptions> options,
        IServiceProvider serviceProvider,
        ILogger<OpenSearchIndexService> logger)
    {
        // ...
    }
}
```

- `IOptions<OpenSearchOptions>`: OpenSearch configuration.
- `IServiceProvider`: used to create a scope to resolve `IDocumentParseBlockRepository` (scoped lifetime).
- `ILogger<OpenSearchIndexService>`: structured logging.
- `ConnectionConfiguration` and `OpenSearchLowLevelClient` are created via `new` in the constructor. The service is registered as a singleton and lives for the lifetime of the process; it does not implement `IDisposable`. In the referenced `OpenSearch.Net` 2.2.0 package, `OpenSearchLowLevelClient` does not implement `IDisposable`; `ConnectionConfiguration` does, but it is not disposed explicitly and is reclaimed when the process exits.

```csharp
// SearchDomainService constructor
public SearchDomainService(
    ISearchIndexService searchIndexService,
    ILogger<SearchDomainService> logger)
```

- `ISearchIndexService`: **non-nullable**, injection guaranteed by the DI container (registered as a Singleton).
- `ILogger<SearchDomainService>`: LogWarning on degradation.

### DI Registration (`Program.cs`)

```csharp
builder.Services.Configure<OpenSearchOptions>(builder.Configuration.GetSection("OpenSearch"));
builder.Services.AddSingleton<ISearchIndexService, OpenSearchIndexService>();
builder.Services.AddScoped<ISearchDomainService, SearchDomainService>();
```

## Data Flows

### Index Write Flow

```
StructaDocParseResultSync.SyncAsync
  → parseService.UpdateStatusAsync(Parsed)
  → IndexBlocksToSearchAsync (best-effort, try/catch LogWarning)
      → scopeProvider.GetRequiredService<ISearchIndexService>()
      → searchIndexService.IndexParseBlocksAsync(parseId, file.Id, file.FileName, file.Subject, file.Grade, file.Year)
          → create scope → resolve IDocumentParseBlockRepository
          → GetByParseIdAsync(parseId)
          → filter empty text_content → build bulk → BulkAsync → log
  → AnalyzeMetadataIfMissingAsync (best-effort)
      → LLM analysis → fileService.UpdateMetadataAsync
      → searchIndexService.UpdateDocumentFileMetadataAsync (sync index)
```

- Chunked parsing of large files (`PersistMergedChunkResultsAsync`) indexes only when `status == Parsed`.
- Failed status (`Failed`) is not indexed.

### Index Deletion Flow

```
DELETE /admin/document-parses/{parseId}
  → DocumentParseEndpoints.DeleteDocumentParse
  → parseService.DeleteParseAsync(parseId)
  → searchIndexService.DeleteParseIndexAsync(parseId) (best-effort)

DELETE /admin/document-files/{id}
  → DocumentFileEndpoints.DeleteDocumentFile
  → fileService.DeleteAsync(id)
  → OSS cleanup (best-effort)
  → searchIndexService.DeleteDocumentFileIndexAsync(id) (best-effort)
```

### Metadata Sync Flow

```
PUT /admin/document-files/{id}/metadata
  → DocumentFileEndpoints.UpdateDocumentFileMetadata
  → fileService.UpdateMetadataAsync(id, subject, grade, year)
  → searchIndexService.UpdateDocumentFileMetadataAsync (best-effort)

LLM automatic analysis (AnalyzeMetadataIfMissingAsync)
  → fileService.UpdateMetadataAsync
  → searchIndexService.UpdateDocumentFileMetadataAsync (best-effort)
```

### Search Flow

```
GET /admin/documents/search
  → DocumentSearchEndpoints.Search
  → parameter validation (empty/too-long query → 400)
  → pageSize = Math.Min(Math.Max(pageSize, 1), 100)
  → construct SearchFilterModel (only when any of subject/grade/year/documentTitle is non-empty)
  → searchService.ExactSearchAsync(query, phrase, filter, pageSize, pageToken)
      → _searchIndexService.ExactSearchAsync(...)
          → BuildSearchBody → _client.SearchAsync → ParseSearchResponse
      → on exception: LogWarning → return empty results
  → build JSON response (results / totalCount / nextPageToken)
```

### Startup Initialization Flow (`Program.cs`)

```
1. DatabaseInitializer.InitializeAsync(dbContext, loggerFactory)  — database initialization (no EF Core Migration)
2. searchIndexService.EnsureIndexAsync() (best-effort, failure does not block startup)
   → EnsureIndexExistsAsync
        → check index existence
            ├─ missing → CreateAsync (BuildIndexBody + _meta.mapping_version=CurrentMappingVersion)
            └─ exists → read the index _meta.mapping_version
                ├─ version missing or < CurrentMappingVersion → delete index → CreateAsync (rebuild with the new mapping version)
                │     ⚠️ rebuilding wipes all indexed documents; parsing must be re-triggered to restore index data
                │     Log: [WRN] OpenSearch index {name} has outdated mapping (expected=v, actual=v_old), recreating
                └─ version matches → skip (normal startup)
```

#### Index mapping version management (added 2026-07-10)

An OpenSearch index's mapping **cannot be modified online** once created (field type changes require rebuilding the index). `EnsureIndexAsync` only checked whether the index exists and skipped if it did — meaning that after code upgrades the mapping, the old index would not be updated automatically, and queries would fail with 400 due to mapping mismatch.

**Solution**: write `mapping_version` into the index `_meta`, compare version numbers at startup, and delete and rebuild the index on mismatch.

- The `CurrentMappingVersion` constant is defined in `OpenSearchIndexManager` and incremented on every mapping change to `BuildIndexBody`
- `BuildIndexBody`'s `_meta` field gains `mapping_version`
- `EnsureIndexExistsAsync` reads the index `_meta.mapping_version`; if missing or lower than the current version, delete and rebuild
- Rebuilding is best-effort: deletion failure → LogWarning, startup not blocked; creation failure after successful deletion → LogWarning, startup not blocked

> **Version history**:
> - v1: initial mapping (V1 retrieval capabilities)
> - v2: minerU Gen-2 evolution (adds x0/y0/x1/y1/score/has_image/sub_type/text_level/text_format/caption/_meta.block_data)

> **Historical background**: after deploying minerU Gen-2 on 2026-07-10, the old index (v1 mapping) was incompatible with the new query code and all search requests returned 400. The original `EnsureIndexAsync` only checked index existence, not the mapping version, so the old index could not be updated automatically. After introducing the version-number mechanism, a service restart automatically detects the mismatch and rebuilds once.

## Error Handling Strategy

| Scenario | Handling |
|------|------|
| OpenSearch indexing failure | The Worker catches the exception → LogWarning, parsing flow continues |
| OpenSearch deletion failure | The endpoint catches the exception → LogWarning, deletion flow continues |
| OpenSearch search failure | `SearchDomainService` catches the exception → LogWarning, returns empty results |
| OpenSearch metadata sync failure | The caller catches the exception → LogWarning, no blocking |
| Empty blocks | LogWarning and return, no exception thrown |
| Empty `text_content` | Skip the block (empty text is not indexed) |
| Invalid Base64 `pageToken` | LogDebug, start from the first page |
| HTTP parameter validation failure | Return 400 + error code |

### Search failure diagnostics (added 2026-07-10)

When OpenSearch returns non-200, the `ExactSearchAsync` exception message must include the **response body** (OpenSearch's error JSON); otherwise the root cause of a 400/500 cannot be located from the status code alone.

```csharp
// Correct: the exception message includes the response body
if (!response.Success || response.HttpStatusCode != 200)
{
    var errorBody = response.Body != null ? Encoding.UTF8.GetString(response.Body) : "(empty)";
    throw new InvalidOperationException(
        $"OpenSearch query failed, status code: {response.HttpStatusCode}, response: {errorBody}, query: {json}");
}
```

Also log the sent query JSON at Debug level for reproducibility:

```csharp
_logger.LogDebug("OpenSearch search request: index={Index}, body={Body}", indexName, json);
```

> **Historical background**: after the 2026-07-10 deployment, all search requests were found to return empty results; the logs contained only `OpenSearch query failed, status code: 400`, making the root cause impossible to locate. The root cause was that the exception message lacked the OpenSearch response body. After the fix, the exception message includes the `response` and `query` fields, so `SearchDomainService`'s LogWarning can output complete diagnostic information.

## External Module Interfaces Depended On

| Dependency | Capability provided | Module |
|------|---------|---------|
| `IDocumentParseBlockRepository` | Reads a parse's blocks (index data source) | `Doctheca.Domain.Repositories` |
| `IDocumentParseBlockRepository.GetByParseIdAsync` | Fetches blocks with the same query as V1; no new repository methods | `Doctheca.Domain.Repositories` |
| `IOptions<OpenSearchOptions>` | OpenSearch configuration (Url / IndexName) | `Doctheca.Domain.Models` |
| OpenSearch 2.x | Index storage and search | External service (HTTP) |
| `StructaDocParseWorker` | The best-effort entry point reused for index writes (Generation 2 minerU fields appended in the same bulk) | `Doctheca.Service` |

> The Generation 2 evolution **introduces no** new external dependencies (no new NuGet packages, no new database tables, no new OSS storage, no third-party minerU client changes). Fields in the minerU v1 block schema not covered by the V1 index (`chars`/`position`/`layout_width`, etc.) are persisted as `block_data` jsonb and viewed in the admin UI via the existing `GET /admin/document-files/{id}`; this does not introduce new dependency inversion.

## Generation 2 Evolution Data Flows

### Index Write Evolution (same bulk as V1, minerU fields appended)

```
StructaDocParseWorker.IndexBlocksToSearchAsync (best-effort)
  → scopeProvider.GetRequiredService<ISearchIndexService>()
  → searchIndexService.IndexParseBlocksAsync(parseId, file.Id, file.FileName, file.Subject, file.Grade, file.Year)
      → create scope → resolve IDocumentParseBlockRepository
      → GetByParseIdAsync(parseId)
      → filter empty text_content → build bulk (V1 fields + Generation 2 minerU fields, same _id)
          V1 fields: parse_id, document_file_id, file_name, subject, grade, year,
                    page_number, block_id, block_type, text, sort_index, image_id, created_at
          Generation 2 minerU additions: x0, y0, x1, y1 (bbox components, float), score (float),
                                 has_image (bool), _meta.block_data (object, enabled:false)
      → BulkAsync → log
```

> Data source for the new minerU fields: `block.BlockData` (raw jsonb) already lands in entity fields (ImageId) during the `DocumentParseBlockService.ParseBlock` parsing stage; `bbox`/`score` are deserialized by the parse service and written to the block entity; the indexing side does not re-parse the minerU JSON here.

### Search Evolution (V1 endpoint extension)

```
GET /admin/documents/search
  → DocumentSearchEndpoints.Search
  → parameter validation (empty/too-long query → 400; if all minerU filters are also empty, execute the V1 keyword-only path)
  → pageSize = Math.Min(Math.Max(pageSize, 1), 100)   (V1's 100 unchanged; ≤ 50 recommended with minerU filters, guided by the frontend)
  → construct SearchFilterModel (the 4 original V1 items + Generation 2 additions BlockType/PageNumber/HasImage)
  → searchService.ExactSearchAsync(query, phrase, filter, pageSize, pageToken)
      → _searchIndexService.ExactSearchAsync(...)
          → BuildSearchBody (keyword goes to must; blockType/pageNumber/hasImage go to filter)
                          → _client.SourceIncluding(new[] { "_meta.block_data", ... })
          → _client.SearchAsync
          → ParseSearchResponse (field mapping + minerU field extraction from _source + BlockData attachment)
      → on exception: LogWarning → return empty results
  → build JSON response: results (with the new optional BlockData/Bbox/Score fields) / totalCount / nextPageToken
```

## Testability Design

- **Pure logic extraction**: `BuildIndexBody` / `BuildSearchBody` / `ParseSearchResponse` are `internal static` with no IO dependencies, directly unit-testable.
- **Interface isolation**: `ISearchIndexService` / `ISearchDomainService` are injected via constructors; `SearchDomainService` is verifiable with mocks.
- **Degradation is verifiable**: mock `ISearchIndexService.ExactSearchAsync` to throw and verify `SearchDomainService` returns empty results + LogWarning.
- **Startup initialization is best-effort**: `EnsureIndexAsync` failure does not block service startup; it only logs.
- **Generation 2 minerU filter pure logic is testable**: `BuildSearchBody` adds filter-construction assertions on top of the existing `internal static` tests; `ParseSearchResponse` adds minerU field attachment assertions (`BlockData`/`Bbox`/`Score` extraction) on top of the existing `internal static` tests. **No new** standalone `BuildBlock*` / `ParseBlock*` methods, avoiding double maintenance.
- **Generation 2 degradation is verifiable**: reuses the existing mock-throw path for `ISearchIndexService.ExactSearchAsync` to verify minerU filters still go through `SearchDomainService` degradation, returning empty results + LogWarning.
