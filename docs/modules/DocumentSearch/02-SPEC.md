# DocumentSearch — Detailed Requirement Specification (SPEC)

## Feature Overview

DocumentSearch provides two main capabilities:
1. **OpenSearch block index writes**: indexes the `document_parse_blocks` produced by parsing into OpenSearch, and automatically maintains the index on parse deletion, file deletion, and metadata updates.
2. **Exact keyword search**: provides OpenSearch BM25-based keyword search through the `GET /admin/documents/search` HTTP API, supporting phrase/word matching, metadata filtering, and cursor pagination, and degrading to empty results when OpenSearch is unavailable.

## 1. Interface Changes

### 1.1 ISearchIndexService (`src/Domain/Repositories/ISearchIndexService.cs`)

```csharp
public interface ISearchIndexService
{
    Task EnsureIndexAsync();
    Task IndexParseBlocksAsync(Guid parseId, Guid documentFileId, string fileName, string? subject, string? grade, string? year);
    Task DeleteParseIndexAsync(Guid parseId);
    Task DeleteDocumentFileIndexAsync(Guid documentFileId);
    Task UpdateDocumentFileMetadataAsync(Guid documentFileId, string? subject, string? grade, string? year);
    Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken);
}
```

| Method | Responsibility | When called |
|------|------|---------|
| `EnsureIndexAsync` | Ensures the index exists (creates it if missing) | At service startup (`Program.cs`) |
| `IndexParseBlocksAsync` | Indexes all of a parse's blocks into OpenSearch | After parse completion |
| `DeleteParseIndexAsync` | Deletes index documents by `parse_id` | After deleting a parse record |
| `DeleteDocumentFileIndexAsync` | Deletes index documents by `document_file_id` | After deleting a document file |
| `UpdateDocumentFileMetadataAsync` | Refreshes subject/grade/year by `document_file_id` | After metadata updates (manual or LLM) |
| `ExactSearchAsync` | OpenSearch BM25 search | HTTP search requests |

### 1.2 ISearchDomainService (`src/Domain/Services/ISearchDomainService.cs`)

```csharp
public interface ISearchDomainService
{
    Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken);
}
```

A thin wrapper around `ISearchIndexService.ExactSearchAsync` that degrades to empty results on exceptions.

### 1.3 Domain Models (`src/Domain/Models/`)

```csharp
// SearchResultModel.cs
public class SearchResultModel
{
    public string DocumentName { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public string AssociatedText { get; set; } = string.Empty;
    public double Score { get; set; }
    public string MatchType { get; set; } = string.Empty;
    public string SegmentId { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
}

// SearchFilterModel.cs
public class SearchFilterModel
{
    public string? DocumentTitle { get; set; }
    public string? Subject { get; set; }
    public string? Grade { get; set; }
    public string? Year { get; set; }
}

// SearchMatchType.cs
public static class SearchMatchType
{
    public const string ExactPhrase = "exact_phrase";
    public const string Stemmed = "stemmed";
    public const string ExactWord = "exact_word";
}

// OpenSearchOptions.cs
public class OpenSearchOptions
{
    public string Url { get; set; } = "http://localhost:9200";
    public string IndexName { get; set; } = "doctheca-segments";
}
```

> Note: `SearchResultModel` / `SearchFilterModel` / `OpenSearchOptions` / `SearchMatchType` each live in their own same-named files; `SearchConfig.cs` **does not exist**.

## 2. OpenSearch Index Spec (`BuildIndexBody`)

### 2.1 Settings

```json
{
  "index": { "number_of_shards": 1, "number_of_replicas": 0 },
  "analysis": {
    "analyzer": {
      "english_custom": { "type": "custom", "tokenizer": "standard", "filter": ["lowercase", "english_stop", "english_stemmer"] },
      "english_phrase": { "type": "custom", "tokenizer": "standard", "filter": ["lowercase"] }
    },
    "filter": {
      "english_stop": { "type": "stop", "stopwords": "_english_" },
      "english_stemmer": { "type": "stemmer", "language": "english" }
    }
  }
}
```

- `english_custom`: standard tokenization + lowercasing + stop words + stemming (for word matching, expanded recall).
- `english_phrase`: standard tokenization + lowercasing (for phrase matching, preserves phrase integrity, no stemming).

### 2.2 Mappings

| Field | Type | Description |
|------|------|------|
| `parse_id` | keyword | Deletion by parse |
| `document_file_id` | keyword | Deletion by document |
| `file_name` | keyword | Search result display |
| `block_id` | keyword | Unique identifier |
| `block_type` | keyword | Layout block type |
| `sort_index` | integer | Block order |
| `image_id` | keyword | Image ID |
| `subject` | keyword | Subject filtering |
| `grade` | keyword | Grade filtering |
| `year` | keyword | Year filtering |
| `page_number` | integer | Page number |
| `text` | text (english_custom) | Indexed content; sub-fields `exact` (english_phrase), `keyword` (ignore_above=256) |
| `created_at` | date | Creation time |

> Legacy fields removed: `document_id` / `document_title` / `sentence_id` / `question_id` / `segment_type` / `start_offset` / `end_offset`.

## 3. Index Write Spec (`IndexParseBlocksAsync`)

### 3.1 Input

| Parameter | Type | Description |
|------|------|------|
| `parseId` | Guid | Parse record ID |
| `documentFileId` | Guid | Document file ID |
| `fileName` | string | File name (shown in search results) |
| `subject` | string? | Subject (read from `document_files`) |
| `grade` | string? | Grade |
| `year` | string? | Year |

### 3.2 Data Flow

```
1. Read blocks via IDocumentParseBlockRepository.GetByParseIdAsync(parseId)
2. If blocks are empty → LogWarning and return
3. Iterate blocks, skipping blocks where string.IsNullOrWhiteSpace(TextContent)
4. Build the bulk request (2 lines per block: index instruction + document), _id = $"block_{block.Id}"
5. Document fields: parse_id, document_file_id, file_name, subject, grade, year,
             page_number (= block.PageId), block_id (= block.Id), block_type,
             text (= block.TextContent), sort_index, image_id, created_at
6. Submit via _client.BulkAsync
7. Log both success and failure
```

### 3.3 Idempotency

`_id = $"block_{block.Id}"`; re-indexing the same block overwrites the existing document, so no duplicates are produced.

## 4. Index Deletion Spec

### 4.1 DeleteParseIndexAsync

- Build `delete_by_query`: `{ query: { term: { parse_id: parseId.ToString() } } }`
- Call `_client.DeleteByQueryAsync`
- Log success/failure

### 4.2 DeleteDocumentFileIndexAsync

- Build `delete_by_query`: `{ query: { term: { document_file_id: documentFileId.ToString() } } }`
- Call `_client.DeleteByQueryAsync`
- Log success/failure

> The two delete methods **do not log** `deletedCount`; they only log the success/failure status code.

## 5. Metadata Sync Spec (`UpdateDocumentFileMetadataAsync`)

- Build `update_by_query`: `{ query: { term: { document_file_id } }, script: { source: "ctx._source.subject = params.subject; ...", params: { subject, grade, year } } }`
- Call `_client.UpdateByQueryAsync`
- Log success/failure

## 6. Search Spec (`ExactSearchAsync` / `BuildSearchBody`)

### 6.1 Query Building

| Parameter | Handling |
|------|------|
| `phrase = true` | `match_phrase` query on the `text.exact` field (english_phrase analyzer) |
| `phrase = false` | `match` query on the `text` field (english_custom analyzer, with stemming) |
| filter.Subject | `term: { subject: { value } }` |
| filter.Grade | `term: { grade: { value } }` |
| filter.Year | `term: { year: { value } }` |
| filter.DocumentTitle | `term: { file_name: { value } }` (note: the C# property is named `DocumentTitle`, mapped to the index field `file_name`) |

- With no filter, the query is mainQuery directly; with filters, it is wrapped as `bool { must: mainQuery, filter: [...] }`.
- Filter fields that are empty strings / null **do not participate** in filtering (checked with `string.IsNullOrEmpty` in `BuildSearchBody`).

### 6.2 Sorting and Pagination

- Sorting: `_score desc` → `block_id asc` (guarantees stable pagination).
- Pagination: `search_after` cursor. `pageToken` is the Base64 encoding of the last `sort` array of the previous page; when invalid Base64 fails to decode, the search starts from the first page (LogDebug, no exception thrown).
- `nextToken` generation condition: when `results.Count == pageSize`, the Base64 encoding of the last sort array; otherwise `null`.

### 6.3 Highlighting

- `highlight` enables highlighting on the `text` field, `pre_tags = ["<em>"]`, `post_tags = ["</em>"]`.
- When parsing the response, `highlight.text[0]` is preferred as `AssociatedText`, falling back to `_source.text` when there is no highlight.

### 6.4 Response Parsing (`ParseSearchResponse`)

| Response field | Read from |
|---------|----------|
| `DocumentName` | `_source.file_name` |
| `PageNumber` | `_source.page_number` |
| `AssociatedText` | `highlight.text[0]` (preferred) or `_source.text` |
| `Score` | `_score` (defaults to 0 when missing) |
| `MatchType` | `phrase ? "exact_phrase" : "stemmed"` |
| `SegmentId` | `_source.block_id` |
| `StartOffset` | 0 (blocks have no offset) |
| `EndOffset` | 0 (blocks have no offset) |
| `CreatedAt` | `_source.created_at` (parsed as DateTimeOffset, null on failure) |

- `TotalCount` comes from `hits.total.value`.
- When `_score` is missing, `Score = 0`; missing `_source` fields use defaults.

## 7. HTTP Endpoint Spec

### 7.1 GET /admin/documents/search

Defined in `DocumentSearchEndpoints.cs`, mapped to the path `/admin/documents/search`.

| Parameter | Type | Default | Description |
|------|------|--------|------|
| `query` | string | (required) | Search keywords |
| `phrase` | bool | `false` | Whether to use phrase matching |
| `pageSize` | int | `20` | Results per page (1-100; values over 100 silently truncated) |
| `pageToken` | string? | `null` | Cursor pagination token |
| `subject` | string? | `null` | Filter by subject |
| `grade` | string? | `null` | Filter by grade |
| `year` | string? | `null` | Filter by year |
| `documentTitle` | string? | `null` | Filter by document title (maps to the index field `file_name`) |

**Success response (200 OK)**:
```json
{
  "results": [
    {
      "documentName": "lecture.pdf",
      "pageNumber": 1,
      "associatedText": "<em>Hello</em> world",
      "score": 1.5,
      "matchType": "stemmed",
      "segmentId": "blk-001",
      "startOffset": 0,
      "endOffset": 0,
      "createdAt": "2026-07-04T10:00:00.0000000+00:00"
    }
  ],
  "totalCount": 42,
  "nextPageToken": ""
}
```

**Validation failure response (400 Bad Request)**:
```json
{ "success": false, "message": "Query cannot be empty", "errorCode": "DOCTHECA_QUERY_REQUIRED" }
{ "success": false, "message": "Query exceeds 200 characters", "errorCode": "DOCTHECA_QUERY_TOO_LONG" }
```

> Note: the success response does **not** include a `success` field; error messages are in **English** (not Chinese). `nextPageToken` is an empty string `""` when there are no more results.

### 7.2 Search Degradation

- `SearchDomainService.ExactSearchAsync` catches any exception thrown by `ISearchIndexService.ExactSearchAsync` and returns `(Results: [], TotalCount: 0, NextToken: null)` after a LogWarning.
- `OpenSearchIndexService.ExactSearchAsync` throws `InvalidOperationException` when OpenSearch returns a non-200 status.

## 8. Error Handling Table

| Scenario | Handling |
|------|------|
| Empty query | HTTP 400, `DOCTHECA_QUERY_REQUIRED` |
| Query over 200 characters | HTTP 400, `DOCTHECA_QUERY_TOO_LONG` |
| `pageSize` over the limit | Silently truncated to 100 |
| Empty blocks (at index time) | LogWarning and return, no exception thrown |
| OpenSearch connection failure (index/delete) | Throws; the caller catches it and logs a Warning without blocking the main flow |
| Empty `text_content` | Skip the block |
| OpenSearch search non-200 | Throws `InvalidOperationException`; `SearchDomainService` catches it and returns empty results |
| `pageToken` decode failure | LogDebug, start from the first page |
| Metadata sync failure | LogWarning, no blocking |

## 9. Implementation Steps

### 9.1 Index Writes (`StructaDocParseWorker` Integration)

In `PersistParseResultAsync` and `PersistMergedChunkResultsAsync` (only when `status == DocumentParseStatus.Parsed`):

```csharp
// Parse completed → index blocks (best-effort)
try
{
    var searchIndexService = scopeProvider.GetRequiredService<ISearchIndexService>();
    await searchIndexService.IndexParseBlocksAsync(parse.Id, file.Id, file.FileName, file.Subject, file.Grade, file.Year);
}
catch (Exception ex)
{
    _logger.LogWarning(ex, "Failed to index parse {ParseId} to OpenSearch", parse.Id);
}
```

### 9.2 Index Deletion (`DocumentParseEndpoints` / `DocumentFileEndpoints` Integration)

- `DeleteDocumentParse`: calls `DeleteParseIndexAsync` after the parse record is deleted (best-effort).
- `DeleteDocumentFile`: calls `DeleteDocumentFileIndexAsync` after the database delete (Step 4) (best-effort).
- `UpdateDocumentFileMetadata`: calls `UpdateDocumentFileMetadataAsync` after the metadata update (best-effort).

### 9.3 Metadata Sync (`StructaDocParseWorker.AnalyzeMetadataIfMissingAsync`)

After LLM analysis completes, call `UpdateDocumentFileMetadataAsync(documentFileId, newSubject, newGrade, newYear)` to sync the OpenSearch index (best-effort).

## 10. Configuration

| Section | Key | Default |
|--------|-----|--------|
| `OpenSearch` | `Url` | `http://localhost:9200` |
| `OpenSearch` | `IndexName` | `doctheca-segments` |

- Configuration binding: `builder.Services.Configure<OpenSearchOptions>(builder.Configuration.GetSection("OpenSearch"))`.
- DI registration: `AddSingleton<ISearchIndexService, OpenSearchIndexService>()`.
- Database initialization uses `DatabaseInitializer` (no EF Core Migration).

## 11. Testing Strategy

### 11.1 Unit Tests (UT)

Pure logic tests (`internal static` methods), no OpenSearch HTTP dependency. Implemented in `OpenSearchIndexServiceTests.cs` and `SearchDomainServiceTests.cs`, which underneath call `OpenSearchIndexManager.BuildIndexBody`, `OpenSearchQueryBuilder.BuildSearchBody`, and `OpenSearchResponseParser.ParseSearchResponse` respectively.

- `BuildSearchBody`: query building, filter building, search_after, sort, highlight.
- `ParseSearchResponse`: normal parsing, phrase MatchType, highlight priority, nextToken generation, empty results, defaults for missing fields.
- `BuildIndexBody`: block fields present with correct types, no legacy fields, analyzers retained.
- `SearchDomainService.ExactSearchAsync`: delegation, filter pass-through, pageToken pass-through, exception degradation, empty results.

### 11.2 Integration Tests (Planned)

Depend on real OpenSearch HTTP and a database; not implemented:
- After parse completion, OpenSearch has block data.
- OpenSearch cleanup after deleting a parse / document.
- Search hits block data.

### 11.3 Test Tooling

- Framework: `xUnit` + `Moq` + `FluentAssertions`.
- Connecting to a real OpenSearch for unit tests is prohibited.
- Exact-equality assertions based on timestamps are prohibited.

## 12. Impact Scope

| File | Impact |
|------|------|
| `ISearchIndexService.cs` | 6 method signatures |
| `OpenSearchIndexService.cs` | `ISearchIndexService` facade; actual logic delegated to helper classes under `OpenSearch/` |
| `ISearchDomainService.cs` / `SearchDomainService.cs` | Thin wrapper + degradation |
| `DocumentSearchEndpoints.cs` | `GET /admin/documents/search` |
| `StructaDocParseWorker.cs` | Indexing after parse completion (best-effort) |
| `DocumentParseEndpoints.cs` | Index cleanup after parse deletion |
| `DocumentFileEndpoints.cs` | Index cleanup after file deletion; index sync after metadata updates |
| `Program.cs` | `EnsureIndexAsync` at startup (best-effort) |

---

## 13. Generation 2 — minerU Block Structured Retrieval Spec (Evolving V1)

> Generation 2 reuses the same V1 OpenSearch index (`doctheca-segments`) and the same `StructaDocParseWorker` entry point; **no new index, no parallel endpoint, no standalone `BlockXxx` model classes**. Generation 2 **appends** minerU dimension fields and the embedded `blockData` object during V1 index writes, and **adds** minerU filter parameters and attachment fields on the V1 endpoint `GET /admin/documents/search`; the V1 query path changes zero.

### 13.1 Generation 2 Interface Changes (Extending V1 Components)

#### 13.1.1 Domain Models (extending `src/Domain/Models/`, no standalone classes)

```csharp
// SearchResultModel.cs — Generation 2 adds optional fields (null when old frontends do not send them, backward compatible)
public class SearchResultModel
{
    // ... V1 fields unchanged (including public double Score = OpenSearch _score) ...
    public string? BlockData { get; set; }                  // [Generation 2] raw minerU JSON text (for detail display)
    public float[]? Bbox { get; set; }                      // [Generation 2] minerU bbox [x0,y0,x1,y1]
    public double? MineruScore { get; set; }                // [Generation 2] minerU confidence (VLM backend)
    // [Deviation note] The SPEC originally proposed `float? Score`, but V1 already has `public double Score` (OpenSearch _score);
    // C# does not allow same-named fields, so the Generation 2 field is named `MineruScore` (consistent with `block.MineruScore` in §13.9.1).
    // The HTTP response JSON field name remains `mineruScore`; the frontend reads this key.
    public string? SubType { get; set; }                    // [Generation 2] minerU secondary classification
    public int? TextLevel { get; set; }                     // [Generation 2] 0=body text,1=h1...; null for non-heading
    public string? TextFormat { get; set; }                 // [Generation 2] latex/markdown/none (VLM)
    public string? Caption { get; set; }                    // [Generation 2] concatenated caption text (keyword recall)
}

// SearchFilterModel.cs — Generation 2 adds minerU filter conditions (the 4 original V1 items unchanged)
public class SearchFilterModel
{
    // ... V1 fields (DocumentTitle / Subject / Grade / Year) unchanged ...
    public string? BlockType { get; set; }                  // [Generation 2] exact match on minerU type
    public string? BlockSubType { get; set; }               // [Generation 2] exact match on minerU sub_type
    public int? PageNumber { get; set; }                    // [Generation 2] minerU page_id
    public int? TextLevel { get; set; }                     // [Generation 2] minerU text_level
    public string? TextFormat { get; set; }                 // [Generation 2] minerU text_format
    public Guid? ParseId { get; set; }                      // [Generation 2] narrow to a specific parse
    public Guid? DocumentFileId { get; set; }              // [Generation 2] narrow to a specific document
    public bool? HasImage { get; set; }                     // [Generation 2] select blocks with img_path
}
```

#### 13.1.2 ISearchIndexService Extension (minerU field indexing added on the same interface, method signatures unchanged)

```csharp
public interface ISearchIndexService
{
    // V1's 6 method signatures unchanged; Generation 2 adds minerU fields inside the IndexParseBlocksAsync implementation
    Task IndexParseBlocksAsync(Guid parseId, Guid documentFileId, string fileName,
        string? subject, string? grade, string? year);
    // ... the other 5 methods unchanged ...
}
```

> Generation 2 minerU fields **form the same bulk request as the V1 fields** inside the `IndexParseBlocksAsync` implementation (fields appended under the same `_id = $"block_{block.Id}"`), adding no extra network round trip and no new interface methods.

#### 13.1.3 ISearchDomainService Extension (minerU filter branch extended within the same `ExactSearchAsync`)

```csharp
public interface ISearchDomainService
{
    // V1 signature unchanged; Generation 2 adds the minerU filter branch + attachment field parsing inside the ExactSearchAsync implementation
    Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken);
}
```

#### 13.1.4 HTTP Endpoint (extending `DocumentSearchEndpoints.cs`, no new files)

```csharp
// GET /admin/documents/search — Generation 2 adds optional input parameters (all original V1 parameters query/phrase/subject/grade/year/documentTitle/pageSize/pageToken are retained)
// Added: blockType / pageNumber / parseId / documentFileId / hasImage
// When all minerU filters are null, behavior is exactly equivalent to V1 (zero regression)
public static async Task<IResults> Search(...)
```

### 13.2 Two-Layer Inventory of minerU v1 Block Fields

> `[Note] The real minerU v1 block schema cannot be verified online in this environment (github.com / pypi.org / opendatalab.github.io are all blocked by the gateway). The table below is compiled from the actual read path of `DocumentParseBlockService.ParseBlock` (see [03-DESIGN.md §Generation 2 Evolution](./03-DESIGN.md)).`

#### Layer 1 (read by code, authoritative — enters the OpenSearch mapping)

| minerU field | Type | Purpose | Code reference |
|------------|------|------|---------|
| `type` | string (required) | Block classification; null/empty/non-string → the whole block is discarded | `DocumentParseBlockService.cs:95-100` |
| `page_id` | int (optional, defaults to 0) | Page number, 0-indexed | `DocumentParseBlockService.cs:104-107` |
| `text` | string or nested structure | Text content, highest priority | `DocumentParseBlockService.cs:151` (`ExtractTextContent`) |
| `content` | string or nested structure | Text content, medium priority (fallback when `text` is missing) | Same as above |
| `body` | string or nested structure | Text content, lowest priority (covers table HTML, etc.) | Same as above |
| `img_path` | string (optional) | Read only when `type=image`; relative path inside the ZIP | `DocumentParseBlockService.cs:119-132` |

#### Layer 2 (`block_data` fallback pass-through JSONB, not parsed by code — `[To be confirmed]`)

| Field | Evidence type |
|------|---------|
| `angle` | `[Inferred]` — stated in the `docs/database/tables/document_parse_blocks.md:42` documentation; no code reads it |
| `formula_latex` | `[Inferred]` — same as above |

> `[Note] The real minerU v1 block schema cannot be verified online in this environment (github.com / pypi.org / opendatalab.github.io are all blocked by the gateway). The first release follows the project code's actual read path (`DocumentParseBlockService.ParseBlock`) plus the MinerU public enumeration consensus. Other minerU fields (`chars`/`position`/`layout_width`/`images`/`table_html`, etc.) are persisted as `block_data` jsonb, viewable in the admin UI via the existing `GET /admin/document-files/{id}`, and deferred until facet requirements are clear.`

### 13.3 Generation 2 Index Write Spec (extending `IndexParseBlocksAsync`, no new methods)

- **Shares the same bulk request** with V1's `IndexParseBlocksAsync` (fields appended, no extra network round trip).
- On top of the V1 fields, each block document gains:
  - `x0` / `y0` / `x1` / `y1` (float) — minerU `bbox` components (facilitate range queries)
  - `score` (float) — minerU confidence
  - `has_image` (boolean) — whether `img_path` is non-empty (determined via `block.ImageId != null`, without re-parsing the minerU JSON)
  - `sub_type` (string) — official minerU field distinguishing secondary block classifications (caption/body/footnote, etc.)
  - `text_level` (int) — official minerU field, heading level (0 = body text, 1 = h1, 2 = h2...; non-heading text is written as `-1`)
  - `text_format` (string) — minerU VLM-backend-specific field (`latex` / `markdown` / `none`); indexed as an empty string when the pipeline version lacks this field
  - `caption` (text, english_custom) — derived field: concatenates `image_caption` / `table_caption` / `chart_caption` / `code_caption` texts into searchable text
  - `_meta.block_data` (object, `enabled:false`) — the raw minerU JSON attached whole, **stored but not indexed** (avoids write amplification, NFR-11)
- ⚠️ **bbox coordinate system difference** (minerU official): the pipeline backend normalizes `bbox` to 0-1000, the VLM backend normalizes `bbox` to 0-1 (percentage). Before writing to OpenSearch, `DocumentParseBlockService.ParseBlock` should normalize uniformly to 0-1000 (the pipeline convention) to avoid coordinate ambiguity in frontend rendering / mixed VLM-pipeline index scenarios.
- minerU `type` shares its source with V1 `block_type`, `page_id` with V1 `page_number`, `text|content|body` with V1 `text` → **no new** alias fields.
- `_id = $"block_{block.Id}"` is idempotent (same as V1).
- Blocks with empty `text_content` are skipped in V1; Generation 2 still skips them (nothing searchable).
- best-effort: failures only LogWarning and do not block the main parsing flow.

### 13.4 Generation 2 OpenSearch Mapping Additions

Appended to the V1 `BuildIndexBody` mappings:

```json
"x0":                 { "type": "float" },
"y0":                 { "type": "float" },
"x1":                 { "type": "float" },
"y1":                 { "type": "float" },
"score":              { "type": "float" },
"has_image":          { "type": "boolean" },
"sub_type":           { "type": "keyword" },
"text_level":         { "type": "integer" },
"text_format":        { "type": "keyword" },
"caption":            { "type": "text", "analyzer": "english_custom",
                          "fields": { "keyword": { "type": "keyword", "ignore_above": 256 } } },
"_meta":              { "type": "object", "enabled": true, "dynamic": false,
                          "properties": { "block_data": { "type": "object", "enabled": false } } }
```

> `_meta.block_data` uses `enabled:false` — the content is stored in `_source` but **not indexed**, and is attached from `_source` at query time, satisfying NFR-11.
>
> `sub_type` is an official minerU field (from `content_list.json`) used to distinguish secondary classifications such as caption/body/footnote (e.g., `image_caption` / `image_body` / `table_footnote` / `code` / `algorithm` / `text` / `ref_text`).
>
> `text_level` is an official minerU field: `0` = body text, `1` = h1 heading, `2` = h2 heading, and so on; the field is absent for non-heading text blocks (written as `-1` at index time).
>
> `text_format` is a minerU VLM-backend-specific field: `latex` / `markdown` / `none`, used to distinguish the format of interline equations.
>
> `caption` is a derived field: it concatenates caption texts such as `image_caption` / `table_caption` / `chart_caption` / `code_caption` into one searchable text so keyword search can hit figure/table caption content.

### 13.5 Generation 2 HTTP Endpoint Spec (extending `GET /admin/documents/search`, no new endpoints)

#### GET /admin/documents/search (Generation 2 extension)

Defined in `DocumentSearchEndpoints.cs` (extending the V1 file, no new files). All original V1 parameters are retained, with **added** optional minerU filter parameters:

| Parameter | Type | Default | Description |
|------|------|--------|------|
| `blockType` | string? | `null` | Exact match on minerU `type` (maps to index field `block_type`); see the §13.3 two-layer minerU field inventory for enumerations |
| `blockSubType` | string? | `null` | Exact match on minerU `sub_type` (distinguishes caption/body/footnote, e.g., `table_caption`, `code`, `algorithm`) |
| `pageNumber` | int? | `null` | Exact match on minerU `page_id` (maps to index field `page_number`) |
| `textLevel` | int? | `null` | Exact match on minerU `text_level` (`0` = body text, `1` = h1 heading...; pass `-1` for "non-heading text only") |
| `textFormat` | string? | `null` | Exact match on minerU `text_format` (`latex` / `markdown` / `none`, VLM-backend-specific) |
| `parseId` | Guid? | `null` | Narrow to a specific parse |
| `documentFileId` | Guid? | `null` | Narrow to a specific document |
| `hasImage` | bool? | `null` | Select blocks with `img_path` (maps to index field `has_image`) |

**Validation rules**:
- Original V1 validation (empty/too-long query → 400) is **retained**.
- When all minerU filters are null, behavior is **exactly equivalent to V1** (zero regression).
- `pageSize` over the limit is silently truncated to 100 (the V1 value); with minerU filters the frontend should guide toward ≤ 50.

**Success response (200 OK)** (V1 fields + Generation 2 additions):

```json
{
  "results": [
    {
      "documentName": "lecture.pdf",
      "pageNumber": 1,
      "associatedText": "<em>As shown in the figure</em>, AB is the diameter of circle O...",
      "score": 1.5,
      "matchType": "stemmed",
      "segmentId": "blk-001",
      "startOffset": 0,
      "endOffset": 0,
      "createdAt": "2026-07-04T10:00:00.0000000+00:00",
      "blockData": "{\"type\":\"text\",\"text_level\":0,\"page_idx\":0,\"text\":\"...\",\"bbox\":[100,200,300,400]}",
      "bbox": [100.0, 200.0, 300.0, 400.0],
      "mineruScore": 0.97,
      "subType": null,
      "textLevel": 0,
      "textFormat": null,
      "caption": null
    }
  ],
  "totalCount": 17,
  "nextPageToken": ""
}
```

> Note: the success response does **not** include a `success` field; error messages are in **English**; `nextPageToken` is an empty string `""` when there are no more results. `blockData` / `bbox` / `score` / `subType` / `textLevel` / `textFormat` / `caption` are optional fields; they are not displayed when old frontends do not send them, and deserialization is unaffected.
>
> ⚠️ **bbox coordinate system**: the response `bbox` is 0-1000 normalized (the pipeline convention); when rendering, the frontend must restore pixel coordinates using `page_size` (from the `document_files` record via `GET /admin/document-files/{id}`). The VLM backend's 0-1 percentage coordinates are normalized uniformly to 0-1000 before indexing.

### 13.6 Generation 2 Query Building (extending `BuildSearchBody`, no standalone methods)

| Parameter | Handling |
|------|------|
| `keyword` non-empty | `match` query on the `text` field (english_custom analyzer; minerU `text|content|body` already lands in V1 `text`, same source, not duplicated) |
| `blockType` non-empty | `term: { block_type: { value } }` (minerU `type` and V1 `block_type` share the same source) |
| `blockSubType` non-empty | `term: { sub_type: { value } }` (minerU `sub_type`, distinguishes caption/body/footnote) |
| `pageNumber` non-empty | `term: { page_number: { value } }` (minerU `page_id` and V1 `page_number` share the same source) |
| `textLevel` non-empty | `term: { text_level: { value } }` (minerU `text_level`, -1 = non-heading text) |
| `textFormat` non-empty | `term: { text_format: { value } }` (VLM-backend-specific: `latex` / `markdown` / `none`) |
| `parseId` non-empty | `term: { parse_id: { value } }` |
| `documentFileId` non-empty | `term: { document_file_id: { value } }` |
| `hasImage` non-empty | `term: { has_image: { value } }` |

- Multiple conditions combine as `bool { must: [...], filter: [... }` (keyword goes to `must`, minerU exact terms go to `filter`; V1 without keyword has no minerU filters and follows the V1 path).
- Sorting: `_score desc` (with keyword) → `block_id asc` (stable pagination); without keyword, `sort_index asc`.
- Pagination: `search_after` cursor (same as V1).
- Highlighting: enabled on the `text` field (`<em>...</em>`, reusing the V1 highlight field; minerU `text|content|body` already shares its source with V1 `text_content`, no need for a separate `mineru_text`).
- `_source` includes `x0/y0/x1/y1/score/has_image` + `_meta.block_data`.

### 13.7 Generation 2 Response Parsing (extending `ParseSearchResponse`, no standalone methods)

A minerU attachment branch is appended at the end of the V1 `ParseSearchResponse` parsing logic:

| Generation 2 added response field | Read from |
|-------------------|----------|
| `BlockData` (string?) | `_source._meta.block_data` (raw string, new in Generation 2) |
| `Bbox` (float[]?) | `[_source.x0, _source.y0, _source.x1, _source.y1]` (new in Generation 2) |
| `MineruScore` (double?) | `_source.score` (new in Generation 2; the C# property is named `MineruScore` to avoid conflicting with V1's `Score` (OpenSearch `_score`); see the deviation note in §13.1.1) |
| `SubType` (string?) | `_source.sub_type` (new in Generation 2, secondary classification such as caption/body/footnote) |
| `TextLevel` (int?) | `_source.text_level` (new in Generation 2, heading level, -1 = non-heading text) |
| `TextFormat` (string?) | `_source.text_format` (new in Generation 2, `latex`/`markdown`, etc., VLM-backend-specific) |
| `Caption` (string?) | `_source.caption` (new in Generation 2, concatenated caption texts such as image_footnote/table_caption, for keyword hits) |

- `TotalCount` comes from `hits.total.value` (V1 unchanged).
- `nextToken` generation condition is the same as V1 (`results.Count == pageSize`).
- When `_meta.block_data` is missing, falls back to `BlockData = null` (no exception thrown, response not blocked).

### 13.8 Generation 2 Error Handling Table (additions)

| Scenario | Handling |
|------|------|
| All minerU filters null | Takes the V1 path, behavior exactly equivalent (zero regression) |
| `pageSize` over the limit | Silently truncated to 100 (V1 value unchanged) |
| OpenSearch search non-200 | Throws `InvalidOperationException`; `SearchDomainService` catches it and returns empty results (V1 degradation path) |
| `_meta.block_data` missing | `BlockData = null`, response not blocked |
| `pageToken` decode failure | LogDebug, start from the first page (V1 unchanged) |

> Note: the `DOCTHECA_FILTER_REQUIRED` error code is **dropped** — the Generation 2 endpoint shares `GET /admin/documents/search` with V1; V1's required-`query` validation (empty → 400 `DOCTHECA_QUERY_REQUIRED`) is **retained** as the sole required-field guard, and no separate required-filter guard is needed.

### 13.9 Generation 2 Implementation Steps (extending V1, nothing new built)

#### 13.9.1 Index Write Extension (`StructaDocParseWorker`, extending `IndexParseBlocksAsync`)

In `IndexBlocksToSearchAsync`, minerU dimension fields are appended after the V1 bulk is built:

```csharp
// Append Generation 2 minerU dimension fields after the V1 fields are built
x0            = block.BboxX0,
y0            = block.BboxY0,
x1            = block.BboxX1,
y1            = block.BboxY1,
score         = block.MineruScore,
has_image     = block.ImageId != null,
_meta         = new { block_data = block.BlockData }   // attached whole (V1 minerU same-source fields already cover block_type/page_number/text)
```

> `BboxX0/Y0/X1/Y1` and `MineruScore` are extracted from the minerU `block_data` and written to the block entity during the `DocumentParseBlockService.ParseBlock` stage; the indexing side does not re-parse the JSON. `has_image` is determined via `block.ImageId != null`, avoiding repeated parsing of the minerU JSON.

#### 13.9.2 Domain Service and Endpoint Extensions

- `ISearchIndexService` / `OpenSearchIndexService`: minerU fields added **within the same `IndexParseBlocksAsync` implementation**; `BuildIndexBody` gains minerU dimension mappings; the minerU filter branch added **within the same `ExactSearchAsync` implementation** + minerU attachment in `ParseSearchResponse`. **No new interface methods, no standalone `Build/Parse` methods.**
- `ISearchDomainService` / `SearchDomainService`: minerU filter pass-through and attachment field parsing extended within the same `ExactSearchAsync`; the degradation pattern reuses V1. **No new `BlockXxx` methods.**
- `DocumentSearchEndpoints.cs`: the same `GET /admin/documents/search` endpoint gains optional minerU inputs (behavior equals V1 when parameters are null). **No new endpoint files.**

#### 13.9.3 Frontend Extension

- The doctheca built-in frontend **modifies** the existing `SearchPage.vue` (the V1 page is retained), adding an "Advanced filters" drawer + result-row expansion of `blockData`/`bbox`/`score` details. **No new** parallel `BlockSearchPage.vue`.
- Ruoyu.Admin is untouched.

### 13.10 Generation 2 Configuration

Reuses V1 `OpenSearch:Url` / `OpenSearch:IndexName`; no new configuration sections.

### 13.11 Generation 2 Testing Strategy

#### 13.11.1 Unit Tests (UT) — added to existing test files

No standalone test classes; minerU assertions are added to the existing `OpenSearchIndexServiceTests.cs` / `SearchDomainServiceTests.cs`:

- `BuildSearchBody` assertions added: minerU filter conditions (`blockType` / `pageNumber` / `hasImage`) go to `filter` clauses; with no minerU filters the `BuildSearchBody` output is identical to V1.
- `ParseSearchResponse` assertions added: `_source._meta.block_data` maps correctly to `BlockData`; `x0/y0/x1/y1` combine into `Bbox`; falls back to null when missing.
- `SearchDomainService.ExactSearchAsync` assertions added: minerU filters are passed through to the index service; exception degradation still returns empty results.

#### 13.11.2 Integration Tests (planned, same integration test suite as V1)

- After parse completion, OpenSearch has `x0/y0/x1/y1`/`score`/`has_image`/`_meta.block_data`.
- `GET /admin/documents/search?keyword=X&blockType=Y` hits the correct blocks, and the response attaches `blockData`.
- The attached `blockData` is consistent with database storage (round-trip).

### 13.12 Generation 2 Impact Scope (after evolution)

| File | Impact |
|------|------|
| `ISearchIndexService.cs` | V1's 6 method signatures unchanged; the implementation gains minerU field indexing (Generation 2) |
| `OpenSearchIndexService.cs` | The V1 facade gains minerU fields; `OpenSearchIndexManager.BuildIndexBody` gains minerU dimension mappings; minerU filter + attachment branches added inside `OpenSearchQueryBuilder.BuildSearchBody` / `OpenSearchResponseParser.ParseSearchResponse` |
| `ISearchDomainService.cs` / `SearchDomainService.cs` | minerU filter pass-through and attachment field parsing added inside `ExactSearchAsync` (extended within the V1 method) |
| `DocumentSearchEndpoints.cs` (extension, same file) | `GET /admin/documents/search` gains optional minerU inputs (original V1 inputs and validation retained) |
| `StructaDocParseWorker.cs` | `IndexBlocksToSearchAsync` gains minerU dimension fields (same bulk) |
| `SearchPage.vue` (doctheca built-in frontend extension, same page) | Adds the "Advanced filters" drawer + result-row detail expansion |
| `Program.cs` | No changes (reuses V1 DI registration) |
