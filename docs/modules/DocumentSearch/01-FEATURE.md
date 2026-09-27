# DocumentSearch — OpenSearch Block Indexing and Exact Search

## Feature Overview

Indexes the layout blocks produced by parsing (`document_parse_blocks`) into OpenSearch and automatically maintains the index on parse completion/deletion/metadata changes; also provides exact keyword search based on OpenSearch BM25 through an HTTP API, supporting phrase matching, word matching, metadata filtering, and cursor pagination.

This module merges all capabilities of the former ExactSearch and OpenSearchBlockIndexing modules.

> This document covers two generations of capabilities in an **evolutionary** rather than parallel relationship:
> - **Generation 1 (implemented)**: index writes + exact keyword search (FR-01 ~ FR-09, existing cases in 05-TESTS).
> - **Generation 2 (newly planned in this section)**: on top of Generation 1, **evolve** block-level structured retrieval over minerU v1 JSON — the same OpenSearch index gains minerU dimension fields, the same endpoint `GET /admin/documents/search` gains blockType/pageNumber/hasImage filter parameters + hits carrying `blockData`/`bbox`/`score`, and the same frontend `SearchPage.vue` gains an "Advanced filters" drawer. **No new** parallel endpoint, no new standalone `BlockXxx` model classes, no new parallel search page.

## Background

Doctheca document parsing uses the StructaDoc pipeline (ADR-0009; existing data consists of historical MinerU artifacts), with the tables `document_files` / `document_parses` / `document_parse_blocks` / `document_parse_images`. Search is based on the layout blocks (blocks) of parse artifacts: each block becomes one OpenSearch document, with index fields taken from the `document_parse_blocks` table and metadata from the `document_files` table.

Indexing and search share the same OpenSearch mapping (`BuildIndexBody`), ensuring written fields and queried fields stay consistent. All index operations are best-effort: failures only log a Warning and do not block the main parsing/deletion flows. Search degrades to empty results when OpenSearch is unavailable.

## User Stories

- **As a teacher**: after I upload lecture notes and parsing completes, search can immediately retrieve the lecture content.
- **As a teacher**: I filter the search scope by subject/grade/year to quickly locate target documents.
- **As an ops engineer**: when I delete a parse result, the corresponding OpenSearch index is cleaned up automatically, leaving no dirty data.
- **As an ops engineer**: when I delete an entire document, the OpenSearch indexes of all its parse versions are cleaned up automatically.
- **As an ops engineer**: after I manually update document metadata (or LLM auto-analysis runs), the metadata in the OpenSearch index is refreshed in sync.

### Generation 2 User Stories (evolving V1, block-level structured retrieval)

- **As a teacher**: I want to filter in the admin UI by keyword + block type (e.g., "text block" / "image block") to quickly locate all matching blocks in a lecture, and directly see the block's complete fields in the raw minerU JSON (bbox / page / type…) so I can verify whether MinerU parsed it correctly.
- **As an ops engineer**: I want to filter across documents by minerU fields (within the same search entry point) — e.g., all `type=equation` blocks, all blocks with images, high-confidence blocks — to audit parse coverage and quality.
- **As a technical lead**: I confirm the evolution extends the existing `GET /admin/documents/search` instead of opening `GET /admin/documents/blocks` or creating standalone `BlockXxx` models / a separate frontend page, avoiding splitting retrieval logic into two systems to maintain.

## Functional Requirements

### FR-01: Index blocks after parse completion
- After the parse status becomes `parsed`, all `document_parse_blocks` of that parse are automatically indexed into OpenSearch.
- Indexing uses `block_{blockId}` as `_id`, supporting idempotent overwrites (re-indexing does not produce duplicate documents).
- Only blocks with non-empty, non-whitespace `text_content` are indexed (`null` / empty string / whitespace-only has nothing searchable; checked with `string.IsNullOrWhiteSpace`).

### FR-02: Multiple parses of one document indexed separately
- The same `document_file_id` can have multiple `parse_id`s (different model versions or re-parsing).
- Each completed parse is indexed independently, and each parse's blocks are stored separately (`_id` contains `blockId`, so no conflicts).

### FR-03: Clean up the index when a parse result is deleted
- When `DELETE /admin/document-parses/{parseId}` fires, all OpenSearch index documents of that parse are deleted by `parse_id`.
- Cleanup failure does not block the delete operation (Warning logged only).

### FR-04: Clean up the index when a document is deleted
- When `DELETE /admin/document-files/{id}` fires, the OpenSearch index documents of all parses of that document are deleted by `document_file_id`.
- Cleanup failure does not block the delete operation (Warning logged only).

### FR-05: Sync the index after metadata updates
- After a manual metadata update (`PUT /admin/document-files/{id}/metadata`) or LLM auto-analysis, the `subject` / `grade` / `year` fields in the OpenSearch index are refreshed by `document_file_id`.
- Failures only log a Warning and do not block.

### FR-06: Index field mapping
- Each block becomes one OpenSearch document with the field mapping:
  - `parse_id` (keyword) — supports deletion by parse
  - `document_file_id` (keyword) — supports deletion by document
  - `file_name` (keyword) — document name (shown in search results)
  - `subject` / `grade` / `year` (keyword) — metadata filtering (at index time the file's existing metadata is read from the `document_files` table; if the file has none yet, an empty string `""` is written, so filtering by that field will not match at that point)
  - `page_number` (integer) — page number (taken from `block.PageId`)
  - `block_id` (keyword) — unique identifier
  - `block_type` (keyword) — layout block type
  - `text` (text, english_custom analyzer) — indexed content (from `text_content`)
  - `sort_index` (integer) — block order
  - `image_id` (keyword) — image ID
  - `created_at` (date) — creation time

### FR-07: Exact keyword search
- `GET /admin/documents/search` provides OpenSearch BM25-based keyword search.
- Supports phrase matching (`phrase=true`, using the `text.exact` field) and word matching (`phrase=false`, using the `text` field).
- Supports metadata filtering by `subject` / `grade` / `year` / `documentTitle`.
- Supports cursor pagination (`search_after`; `pageToken` is the Base64 encoding of the last sort array of the previous page).

### FR-08: Search degradation
- When OpenSearch is unavailable or a query fails, `SearchDomainService` returns empty results and logs a LogWarning without interrupting the request.

### FR-09: Search parameter validation
- An empty query returns HTTP 400 (`DOCTHECA_QUERY_REQUIRED`).
- A query over 200 characters returns HTTP 400 (`DOCTHECA_QUERY_TOO_LONG`).
- `page_size` defaults to 20, minimum 1, maximum 100 (values over 100 are silently truncated).

### FR-10: Generation 2 — extend the V1 endpoint with minerU field filtering
- The existing `GET /admin/documents/search` **gains** optional filter parameters (all original V1 parameters query/phrase/subject/grade/year/documentTitle are retained):
  - `blockType` (string?, exact match on minerU `type`, maps to index field `block_type`)
  - `pageNumber` (int?, exact match on minerU `page_id`, maps to index field `page_number`)
  - `hasImage` (bool?, selects blocks with `img_path`, maps to index field `has_image`)
  - `parseId` (Guid?, narrows to a specific parse)
  - `documentFileId` (Guid?, narrows to a specific document)
- Filter conditions **combine with `AND`** with the V1 keyword conditions (keyword goes to `must`, minerU exact terms go to `filter`).
- When all minerU filters are null, behavior is **exactly equivalent to V1** (zero regression).
- Cursor pagination reuses the V1 `search_after` pattern (`pageToken` is the Base64 of the last `sort` array).

### FR-11: Generation 2 — attach the raw minerU block JSON to search hits
- Each search result gains `blockData` (the raw JSON string from `document_parse_blocks.block_data`), letting the frontend display the hit block's complete minerU fields directly without a second request to `GET /admin/documents/files/{id}`.
- Also gains `bbox` (float[4]) and `score` (float) fields so the admin UI can verify visual regions and confidence.
- V1's `SearchResultModel` is **extended backward-compatibly**: `BlockData` / `Bbox` / `Score` are added as optional fields; frontends that do not send/display them (including existing Ruoyu.Admin references) are unaffected. **No new** standalone `BlockResultModel` class.

### FR-12: Generation 2 — minerU fields enter the OpenSearch mapping
- V1's `BuildIndexBody` gains minerU dimension fields (see [02-SPEC.md §13.4](./02-SPEC.md)):
  - `x0` / `y0` / `x1` / `y1` (float, bbox components, facilitate range queries)
  - `score` (float, minerU confidence)
  - `has_image` (bool, whether `img_path` is non-empty)
  - `_meta.block_data` (object, `enabled:false`, stored but not indexed, used for attachment)
- minerU `type` shares its source with V1 `block_type`, `page_id` with V1 `page_number`, `text|content|body` with V1 `text` → **no new** alias fields; only existing V1 fields are exposed as filter parameters.
- `[Note] The real minerU v1 block schema cannot be verified online in this environment (github.com / pypi.org / opendatalab.github.io are all blocked by the gateway). The first release follows the project code's actual read path (`DocumentParseBlockService.ParseBlock`) plus the MinerU public enumeration consensus; other minerU fields (`chars`/`position`/`layout_width`/`images`/`table_html`, etc.) are persisted as `block_data` jsonb, viewable in the admin UI via the existing `GET /admin/document-files/{id}`, and deferred until facet requirements are clear.`

### FR-13: Generation 2 — admin UI block search entry point (evolving SearchPage.vue)
- The doctheca **built-in frontend** (`frontend/`) **modifies** the existing `SearchPage.vue`, adding an "Advanced filters" drawer (minerU field filtering) + result-row expansion showing `blockData`/`bbox`/`score`. **No new** parallel `BlockSearchPage.vue` page.
- Query panel (V1 keyword input retained, additions): blockType dropdown (candidate values aggregated from historical data), pageNumber numeric input, parseId / documentFileId text inputs, hasImage checkbox.
- Result table columns (V1 columns retained, additions): block type / page number / extracted text (first 80 characters) / details button (modal showing formatted `blockData` JSON + bbox + score).
- Selected rows can navigate to `ParseResultsPage.vue` (already exists) to locate the corresponding parse.
- **Ruoyu.Admin is untouched** (Ruoyu.Admin currently has no doctheca-related views and no BFF forwarding; migrating to Ruoyu.Admin is a separate large project that this module's documentation does not cover).

## Acceptance Criteria

| AC | Description |
|----|------|
| AC-01 | After parse completion, the parse's blocks appear in the OpenSearch index |
| AC-02 | Multiple parses of the same document index each parse's blocks independently with non-conflicting `_id`s |
| AC-03 | After `DELETE /admin/document-parses/{parseId}`, that parse's OpenSearch index documents are deleted |
| AC-04 | After `DELETE /admin/document-files/{id}`, the OpenSearch index documents of all the document's parses are deleted |
| AC-05 | After `PUT /admin/document-files/{id}/metadata`, subject/grade/year in the OpenSearch index are refreshed in sync |
| AC-06 | `GET /admin/documents/search` can find blocks produced by parsing |
| AC-07 | Phrase matching uses the `text.exact` field; word matching uses the `text` field |
| AC-08 | Search returns empty results with a LogWarning when OpenSearch is unavailable |
| AC-09 | Indexing failure does not block the parsing flow (Warning logged only) |
| AC-10 | Index deletion failure does not block the delete operation (Warning logged only) |
| AC-11 | An empty/too-long query returns HTTP 400 with the corresponding error code |
| AC-12 | `page_size` over 100 is silently truncated to 100 |

### Generation 2 Acceptance Criteria (evolving V1)

| AC | Description |
|----|------|
| AC-13 | `GET /admin/documents/search?keyword=X&blockType=Y` filters by minerU type and hits the correct blocks |
| AC-14 | `GET /admin/documents/search` filters exactly by `pageNumber` and hits the correct blocks |
| AC-15 | `GET /admin/documents/search` with `hasImage=true` selects blocks carrying images |
| AC-16 | `GET /admin/documents/search` narrows scope by `parseId` / `documentFileId` |
| AC-17 | V1 zero regression: when all minerU filters are null, the response is exactly the same as before the change |
| AC-18 | `GET /admin/documents/search` hits attach `blockData` (round-trip identical to the `block_data` stored in the database) |
| AC-19 | `GET /admin/documents/search` hits attach `bbox` / `score` fields |
| AC-20 | Cursor pagination works stably over minerU-filtered hit sets (consecutive pages have no duplicates/omissions) |
| AC-21 | The `SearchPage.vue` advanced-filters drawer + result-row `blockData` expansion modal render correctly |
| AC-22 | Before full Generation 2 minerU schema coverage, other minerU fields (chars/position/table_html, etc.) are persisted as `block_data` jsonb and do not block the first release |

## Non-Functional Requirements

| NFR | Description |
|-----|------|
| NFR-01 | Index operations are best-effort and do not block parse status updates |
| NFR-02 | Index/delete failures only log and do not affect the main flow |
| NFR-03 | Index deletion uses `delete_by_query`, supporting batch deletion by `parse_id` / `document_file_id` |
| NFR-04 | Returns empty results when search is unavailable, without interrupting the request |
| NFR-05 | All operations log structured entries containing `parseId` / `documentFileId` / `blockCount` |
| NFR-06 | Search P95 < 200ms |
| NFR-07 | HTTP API error messages do not expose internal exception stacks |

### Generation 2 Non-Functional Requirements (evolving V1)

| NFR | Description |
|-----|------|
| NFR-08 | minerU `type` shares its source with the existing index `block_type`, `page_id` with `page_number`, `text|content|body` with `text` → no re-extraction at write time; only genuinely new dimensions are added (bbox/score/has_image/block_data) |
| NFR-09 | Queries with minerU filters default to a per-page cap of 50 (each block carries the full `blockData` JSONB, avoiding oversized response bodies); values above are truncated; the V1 keyword-only path keeps 100 |
| NFR-10 | Generation 2 retrieval path P95 < 500ms (carrying `blockData`, relaxing V1's 200ms target; to be converged later via column projection and gzip compression) |
| NFR-11 | Full `blockData` is stored in `_source` as `_meta.block_data` (`enabled:false`), kept out of the full-text index to avoid write amplification |
| NFR-12 | The Generation 2 evolution lives in the doctheca built-in frontend (modifying `SearchPage.vue`) and does not expand Ruoyu.Admin's BFF boundary (scope control for this iteration) |

## Data Sources

- **Index source**: the `document_parse_blocks` table (structured output of parse results), read via `IDocumentParseBlockRepository.GetByParseIdAsync`.
- **Metadata source**: the `subject` / `grade` / `year` fields of the `document_files` table. Read from the file record at index time; updated later in sync via `UpdateDocumentFileMetadataAsync`.
- **Generation 2 new data source**: `document_parse_blocks.block_data` (raw minerU JSONB) → extract `bbox` (→ x0/y0/x1/y1), `score`, `has_image`, `_meta.block_data` (attached whole). minerU `type`/`page_id`/`text|content|body`/`img_path` already landed in the Generation 1 database as `block_type`/`page_number`/`text_content`/`ImageId`; they are not re-parsed here.
- **Code usage view** (minerU v1 block keys actually read by DocumentParseBlockService.ParseBlock): type / page_id / text|content|body / img_path; see [03-DESIGN.md §Generation 2 Evolution](./03-DESIGN.md).

## Interface Inventory

### Generation 1 (unchanged)

| Component | Changes |
|------|------|
| `ISearchIndexService` | Defines 6 methods (unchanged; Generation 2 adds minerU field indexing capability on the same interface) |
| `OpenSearchIndexService` | Implements `ISearchIndexService`; `BuildIndexBody` / `BuildSearchBody` / `ParseSearchResponse` are `internal static` pure logic |
| `ISearchDomainService` | Defines `ExactSearchAsync` (thin wrapper + degradation) |
| `SearchDomainService` | Implements `ISearchDomainService`, returns empty results on exceptions |
| `DocumentSearchEndpoints` | `GET /admin/documents/search` endpoint |
| `StructaDocParseWorker` | Calls `IndexParseBlocksAsync` after parse completion (best-effort) |
| `DocumentParseEndpoints` | `DeleteDocumentParse` calls `DeleteParseIndexAsync` (best-effort) |
| `DocumentFileEndpoints` | `DeleteDocumentFile` calls `DeleteDocumentFileIndexAsync` (best-effort); `UpdateDocumentFileMetadata` calls `UpdateDocumentFileMetadataAsync` (best-effort) |

### Generation 2 Evolution (extending V1 components, no parallel classes)

| Component | Responsibility | When called |
|------|------|---------|
| `ISearchIndexService` / `OpenSearchIndexService` | Adds minerU dimension indexing (fields appended in the same bulk of `IndexParseBlocksAsync`: x0/y0/x1/y1/score/has_image/_meta.block_data); adds the minerU filter branch and `blockData` attachment parsing to `ExactSearchAsync` (existing 6 method signatures unchanged) | Same best-effort entry point as V1 parse completion; HTTP query extension |
| `SearchResultModel` (extending `src/Domain/Models/`) | Adds optional `BlockData` / `Bbox` / `Score` fields (null when old frontends do not send them) | HTTP response contract |
| `SearchFilterModel` (extending `src/Domain/Models/`) | Adds `BlockType` / `PageNumber` / `ParseId` / `DocumentFileId` / `HasImage` filter conditions | HTTP query input contract |
| `DocumentSearchEndpoints` (extending `src/Service/Endpoints/`) | The `GET /admin/documents/search` endpoint gains optional filter inputs (FR-10); behavior equals V1 when parameters are null | Admin UI block search entry point (same page) |
| `SearchDomainService` (extended) | `ExactSearchAsync` gains the minerU filter branch and parses minerU attachment fields; **no new** `BlockXxx` methods | HTTP handler delegate |
| `SearchPage.vue` (doctheca built-in frontend, extended) | The existing search page gains an "Advanced filters" drawer + result-row minerU detail expansion; **no new** parallel `BlockSearchPage.vue` | Frontend admin UI |

## Document Index

| Document | Description |
|------|------|
| [01-FEATURE.md](./01-FEATURE.md) | Feature overview, user stories, acceptance criteria (this document) |
| [02-SPEC.md](./02-SPEC.md) | Requirement spec, interface changes, implementation steps, error handling, testing strategy |
| [03-DESIGN.md](./03-DESIGN.md) | Design decisions, data flows, dependencies |
| [04-TASKS.md](./04-TASKS.md) | Task breakdown, command quick reference |
| [05-TESTS.md](./05-TESTS.md) | Unit test tables, FR/AC mapping |
| [06-CONVENTIONS.md](./06-CONVENTIONS.md) | Naming, logging, error messages, code style |
| _Generation 2 cross-document decisions_ | minerU field dimension layering (reusing V1 block_type/page_number/text + new bbox/score/has_image/block_data); see [03-DESIGN.md §Generation 2 Evolution](./03-DESIGN.md) |
