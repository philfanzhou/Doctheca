# DocumentSearch — Conventions and Standards (CONVENTIONS)

## Naming Conventions

- **Namespaces**: domain code uses `Doctheca.Domain.*`; the service layer uses `Doctheca.Service.*`; the database layer uses `Doctheca.Database.*`.
- **Class names**: domain services use `XxxDomainService`; models use `XxxModel`; match type constants use a `static class` (`SearchMatchType`); interfaces use `IXxxService` / `IXxxRepository`.
- **Method names**: start with a verb, following `[Verb][Noun][Async]`, e.g., `ExactSearchAsync`, `IndexParseBlocksAsync`, `BuildSearchBody`.
- **Return values**: the HTTP endpoint layer returns JSON responses; the domain layer returns tuples `(List<SearchResultModel>, int, string?)`.
- **Private fields**: use the `_camelCase` underscore prefix (e.g., `_searchIndexService`, `_client`).
- **Database/index fields**: lowercase snake_case (e.g., `document_file_id`, `block_id`, `text_content`, `page_number`).
- **OpenSearch `_id`**: `block_{blockId}` format, guaranteeing idempotency.
- **Index name**: configuration-driven (`OpenSearchOptions.IndexName`, default `doctheca-segments`).
- **Configuration keys**: `OpenSearch:Url` / `OpenSearch:IndexName`.

## Generation 2 Logging Conventions (added to the V1 log set)

| Scenario | Level | Message template |
|------|------|---------|
| minerU dimension fields and `block_data` persisted successfully alongside | Debug | `Parse {ParseId} minerU fields + block_data indexed alongside V1 fields (BlockCount={BlockCount})` |
| A single `blockData` block exceeds the threshold (≥64KB) | Warning | `Parse {ParseId} block {BlockId} block_data exceeds 64KB, may impact search response size` |
| All minerU filters null → takes the V1 path | Debug | `All minerU filters null on query '{Query}', using V1 code path only` |

- **No sensitive information in logs**: message templates **must not** include raw block text (`blockData`); only statistics such as hash / size / count, avoiding log injection and large log volumes.
- **Logs include context**: search logs include `documentFileId` / `parseId` / `filterSummary` (excluding the raw keyword), `pageSize`.

## Logging Conventions

| Scenario | Level | Message template |
|------|------|---------|
| Indexing succeeded | Information | `Parse {ParseId} indexed {BlockCount} blocks to OpenSearch (FileId={FileId})` |
| Indexing failed | Warning | `Parse {ParseId} indexing failed, status code: {StatusCode}` |
| Empty blocks | Warning | `Parse {ParseId} has no blocks to index` |
| No indexable blocks | Warning | `Parse {ParseId} has no indexable blocks (all text_content empty)` |
| Parse index deletion succeeded | Information | `Parse {ParseId} search index deleted` |
| Parse index deletion failed | Warning | `Failed to delete parse {ParseId} search index, status code: {StatusCode}` |
| Document index deletion succeeded | Information | `Document file {FileId} search index deleted` |
| Document index deletion failed | Warning | `Failed to delete document file {FileId} search index, status code: {StatusCode}` |
| Metadata sync succeeded | Information | `Document file {FileId} search index metadata updated: Subject={Subject}, Grade={Grade}, Year={Year}` |
| Metadata sync failed | Warning | `Failed to update document file {FileId} search index metadata, status code: {StatusCode}` |
| Search degradation | Warning | `OpenSearch query failed for query '{Query}', returning empty results` |
| pageToken decode failure | Debug | `Failed to decode page token, starting from first page` |
| Index does not exist (created at startup) | Information | `OpenSearch index created: {IndexName}` |
| Index already exists | Information | `OpenSearch index already exists: {IndexName}` |

- **No sensitive information in logs**: full query terms must not be logged (only truncated/statistical information); user identity information must not be logged.
- **Logs include context**: index/deletion logs must include `parseId` or `documentFileId` for troubleshooting.

## Error Message Format Conventions

| Scenario | HTTP | Error code | Message text |
|------|------|--------|---------|
| Empty query | 400 | `DOCTHECA_QUERY_REQUIRED` | `Query cannot be empty` |
| Query over 200 characters | 400 | `DOCTHECA_QUERY_TOO_LONG` | `Query exceeds 200 characters` |

### Generation 2 Error Message Conventions (no new error codes)

> Generation 2 **introduces no new error codes**. Existing V1 validation continues to serve:
> - Empty query → 400 `DOCTHECA_QUERY_REQUIRED` (guard condition)
> - Query over 200 characters → 400 `DOCTHECA_QUERY_TOO_LONG`
>
> All minerU filters are optional; `null`/empty strings do not participate in filtering; when all minerU filters are null the V1 path is taken (zero regression; no dedicated all-empty validation error code needed). `pageSize` over the limit is silently truncated to 100 (the V1 value unchanged); with minerU filters the frontend guides toward ≤ 50.

## Generation 2 Naming Conventions (extending V1, no parallel classes/files)

- **Class names**: do not add `BlockXxxModel` / `BlockXxxEndpoints` / `BlockXxxPage`. All Generation 2 capabilities **extend optional properties** on existing V1 classes (`SearchResultModel`, `SearchFilterModel`, `DocumentSearchEndpoints`, `SearchPage.vue`).
- **New index field naming** (genuinely new dimensions; V1 has no fields with the same semantics): `x0`/`y0`/`x1`/`y1` (float, bbox components), `score` (float), `has_image` (bool), `_meta.block_data` (embedded object, enabled:false).
- **Reused V1 field naming** (same source, no double-write): `block_type` = minerU type, `page_number` = minerU page_id, `text` = minerU text.
- **OpenSearch embedded non-indexed field**: `_meta.block_data` uses `enabled:false` (stored but not indexed), complying with NFR-11.
- **Response DTO naming**: backend → frontend camelCase (consistent with V1); the frontend TS interface extends `SearchResultItem` (the V1 interface) with `blockData?`/`bbox?`/`score?`. **No new** standalone `BlockResultItem` interface.
- **Error codes**: Generation 2 introduces no new error codes — V1's `DOCTHECA_QUERY_REQUIRED` (empty-query guard) continues as the sole required-field guard; the zero-regression design avoids extra empty-filter validation.

## Search Conventions

- **Phrase matching** (`phrase=true`): uses `match_phrase` + the `text.exact` field (english_phrase analyzer, lowercase only, preserves phrase integrity).
- **Word matching** (`phrase=false`): uses `match` + the `text` field (english_custom analyzer, with stemming, expands recall).
- **Sorting**: `_score desc` → `block_id asc` (guarantees stable pagination).
- **Pagination**: `search_after` cursor; `pageToken` is the Base64 encoding of the last sort array of the previous page.
- **filter mapping**: `DocumentTitle` → index field `file_name` (note the name mismatch).
- **filter empty values**: `null` or empty-string fields do not participate in filtering (checked with `string.IsNullOrEmpty` in `BuildSearchBody`).
- **highlight**: `pre_tags = ["<em>"]`, `post_tags = ["</em>"]`; highlight takes priority over `_source.text` when parsing.

### Generation 2 Search Conventions (extending the V1 endpoint, no parallel path)

- **keyword execution**: reuses the V1 `match` / `match_phrase` queries on the `text` field (english_custom / english_phrase analyzers). **No new** `mineru_text` field (minerU `text|content|body` already lands in `text_content`).
- **Fallback sorting without keyword**: by `sort_index asc` (block order).
- **minerU exact-condition combination**: keyword goes to `must`; `blockType` (→ `block_type`) / `pageNumber` (→ `page_number`) / `parseId` / `documentFileId` / `hasImage` (→ `has_image`) go to `filter`.
- **Zero-regression guard**: when all minerU filters are null, the V1 path is taken (output identical to pre-change). **No new** `DOCTHECA_FILTER_REQUIRED` error code — V1's required-`query` validation already serves as the sole guard.
- **blockData attachment**: `_source` contains the raw `_meta.block_data` string; `ParseSearchResponse` (the V1 method) adds mapping to `SearchResultModel.BlockData` + combines `Bbox` (x0/y0/x1/y1) + `Score`. No secondary serialization / deserialization.
- **Pagination**: `pageSize` maximum 100 (the V1 value); with minerU filters the frontend guides toward ≤ 50 (carries the full `blockData` JSONB, avoiding oversized response bodies).

## Generation 2 Conventions for Indexing minerU Fields (evolving V1, no parallel structures)

Field governance boundaries that must be observed when implementing Generation 2:

| Layer | minerU fields | Form entering the OpenSearch mapping | When |
|----|------------|---------------------------|---------|
| Reusing existing V1 fields (same source, zero new mapping cost) | `type` / `page_id` / `text|content|body` / `img_path` | Reuses existing V1 `block_type` / `page_number` / `text`; already indexed in V1 with no alias double-write | Current V1 |
| Genuinely *new* Generation 2 fields | `img_path` (booleanized) → `has_image`; `bbox` → `x0/y0/x1/y1`; `score` | `has_image`(bool) / `x0,y0,x1,y1`(float) / `score`(float); `bbox` split into separate floats to facilitate coordinate range queries | Generation 2 first release |
| Fallback (not in the mapping) | Full raw `block_data` + other minerU fields (`chars`/`position`/`layout_width`/`images`/`angle`/`block_tags`/`content_tags`/`list_items`/`code_body`/`code_language`/`table_body`/`formula_latex`/`table_html`, etc.) | `_meta.block_data` embedded object, `enabled:false` (stored but not indexed), attached from `_source` at query time | Generation 2 first release exposes via attachment |
| Long term (once facets are defined) | minerU `chars`/`position`/`table_html`/`code_language`/`list_items`, etc. | Mapping added per facet requirements | Generation 3 |

- **Authoritative schema source**: the minerU official documentation `docs/zh/reference/output_files.md` (curl `https://raw.githubusercontent.com/opendatalab/MinerU/master/docs/zh/reference/output_files.md`) has been fetched and verified.
- **Naming**: genuinely new fields **do not take** the `mineru_` prefix (V1 same-source fields already cover type/page/text); new dimensions use domain naming directly: `x0/y0/x1/y1`/`score`/`has_image`/`sub_type`/`text_level`/`text_format`/`caption`.
- **bbox coordinate systems**: pipeline backend 0-1000, VLM backend 0-1 percentage — the minerU official documentation clearly distinguishes them. `DocumentParseBlockService.ParseBlock` normalizes uniformly to 0-1000 (the pipeline convention) during the parsing stage before persisting + indexing; the frontend restores pixel coordinates from the file's `page_size` when rendering.
- **`blockData` attachment**: always comes from the raw `document_parse_blocks.block_data` string, not re-serialized at the Generation 2 interface layer (faithful round-trip).
- **`has_image` handling**: determined via `block.ImageId != null` (the `ImageId` navigation is written during the `DocumentParseBlockService.ParseBlock` stage), without re-parsing the minerU JSON.
- **minerU type / sub_type authoritative enumeration**: see [03-DESIGN.md §the minerU `content_list.json` block type authoritative enumeration in Generation 2 Evolution](../DocumentSearch/03-DESIGN.md).

> `[Authoritative] The minerU v1 block schema has been verified via docs/zh/reference/output_files.md. The pipeline backend's 0-1000 bbox and the VLM backend's 0-1 percentage coordinate system difference are explicitly stated in the spec. minerU fields not listed in the mapping (chars/position/layout_width/images/angle/list_items/code_body/table_html, etc.) **are not pretended to exist**; they are exposed only via the block_data fallback, deferred into the mapping until facet requirements are clear.`

## Index Write Conventions

- Only blocks with non-empty `text_content` are indexed (empty text has nothing searchable).
- `_id = $"block_{block.Id}"`, guaranteeing idempotency.
- Bulk requests: 2 JSON lines per block (index instruction + document), separated by `\n`, with a trailing `\n`.
- Deletions use `delete_by_query` (by `parse_id` or `document_file_id`).
- Metadata sync uses `update_by_query` + script (matched by `document_file_id`).
- All OpenSearch write operations are best-effort: callers catch exceptions and LogWarning without blocking the main flow.
- **Failed statuses are not indexed**: the Worker calls `IndexParseBlocksAsync` only when `status == DocumentParseStatus.Parsed`.

### Generation 2 Index Write Conventions (extending V1, no new files/classes)

- **Same bulk**: Generation 2 minerU dimension fields and `block_data` must form the **same** bulk request as the V1 fields; `_id = $"block_{block.Id}"` adds no extra network round trip.
- **No V1 fields lost**: appended fields must not overwrite / remove any existing V1 fields (`text`, `block_type`, `page_number`, etc.), keeping the V1 query path at zero regression.
- **No double-write for same-source fields**: minerU `type`/`page_id`/`text` share their source with V1 `block_type`/`page_number`/`text` and are **no longer** aliased as `mineru_*`; only genuinely new dimensions (`x0/y0/x1/y1`/`score`/`has_image`/`_meta.block_data`) gain mapping entries.
- **`has_image` comes from the `ImageId` navigation**: determined via `block.ImageId != null`, without accessing the raw minerU text.
- **`_meta.block_data` = raw `block.BlockData`**: the whole string is passed through without re-serialization (faithful round-trip).
- **Idempotent**: `_id` unchanged, repeated indexing overwrites, no duplicate documents.

## Bulk / HTTP Client Conventions

## Bulk / HTTP Client Conventions

- `OpenSearchLowLevelClient` is created via `new` in the `OpenSearchIndexService` constructor (uses `ConnectionConfiguration`, 30s request timeout).
- Pure logic is extracted into `internal static` methods (`BuildIndexBody` / `BuildSearchBody` / `ParseSearchResponse`), hosted by `OpenSearchIndexManager`, `OpenSearchQueryBuilder`, and `OpenSearchResponseParser` respectively, for easier unit testing.
- `OpenSearchJsonHelper` provides safe reading helpers for `_source` JSON fields.
- Exposed to the test assembly via `InternalsVisibleTo`.

## Test Tooling and Style

- **Framework**: `xUnit` 2.x.
- **Mocking**: `Moq` 4.x; `ISearchIndexService` is replaced with mocks and must not access a real OpenSearch.
- **Assertions**: `FluentAssertions` (`Should()` style); async methods use `async Task` + `await`.
- **Test naming**: `[MethodUnderTest]_[Scenario]_[ExpectedBehavior]`, e.g., `BuildSearchBody_WithPhraseQuery_UsesMatchPhraseOnTextExact`.
- **Determinism**: pagination assertions verify only counts and sort direction; no exact-equality assertions based on timestamps.
- **Prohibited**: must not connect to a real OpenSearch for unit tests; must not rely on `Task.Wait()` / `.Result`.

## Code Style

- Follow the project's C# style: `PascalCase` namespaces, class names, method names; `_camelCase` private fields.
- File-scoped namespaces; remove unused `using`s.
- Async methods return `Task` / `Task<T>` and end with the `Async` suffix; avoid `async void`.
- Comments/logs/exception messages use **English**; business-domain values (e.g., subject names) may be in Chinese.
- Constructor dependency injection order matches the DI container registration order.
- Domain services do not expose `DbContext` directly or execute SQL; data access goes through repository interfaces.
- `ISearchIndexService` is registered as a **Singleton**; `ISearchDomainService` is registered as **Scoped**.
- Database initialization uses `DatabaseInitializer` (no EF Core Migration).
