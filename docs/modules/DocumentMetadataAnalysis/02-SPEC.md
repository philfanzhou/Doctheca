# 02-SPEC — Document Metadata Analysis detailed specification

## 1. Database changes

### 1.1 New columns on document_files

| Column | Type | Constraint | Description |
|------|------|------|------|
| `subject` | varchar(50) | nullable | Subject: English/Chinese/Math/Physics/Chemistry/Biology/Other (stored values are the Chinese literals defined in `DocthecaConstants`) |
| `grade` | varchar(20) | nullable | Grade: K/G1-G12 |
| `year` | varchar(10) | nullable | Year: 4 digits, e.g. 2024 |

#### Database initialization approach

> Historical record: this section describes the retired raw-SQL initializer. Since 2026-09-30 the schema has been managed by the EF Core migration baseline (`20260930161548_InitialCreate`) with `DocthecaMigrationExecutor` takeover rules; see [Migration History](../../database/migrations.md).

This service **does not use EF Core Migration**. Database initialization is done by `DatabaseInitializer.cs` via raw SQL:

- **Fresh database path** — the `CREATE TABLE IF NOT EXISTS document_files (...)` returned by `GetTableCreationSql("document_files")` already inlines the three columns `subject character varying(50) NULL` / `grade character varying(20) NULL` / `year character varying(10) NULL` (after `updated_at`, before the primary key constraint).
- **Existing database path** — the `ADD COLUMN IF NOT EXISTS` fallback in `EnsureColumnsAsync`:

```sql
ALTER TABLE document_files ADD COLUMN IF NOT EXISTS subject character varying(50) NULL;
ALTER TABLE document_files ADD COLUMN IF NOT EXISTS grade character varying(20) NULL;
ALTER TABLE document_files ADD COLUMN IF NOT EXISTS year character varying(10) NULL;
```

Both paths carry `IF NOT EXISTS` idempotency guards and can be executed repeatedly. `DatabaseInitializer.InitializeAsync` is called at startup in `Program.cs`.

### 1.2 Entity and model mapping

```csharp
// DocumentFileEntity.cs
[Column("subject")]
[MaxLength(50)]
public string? Subject { get; set; }

[Column("grade")]
[MaxLength(20)]
public string? Grade { get; set; }

[Column("year")]
[MaxLength(10)]
public string? Year { get; set; }
```

`DocumentFileModel` adds the corresponding fields in sync. The `private static MapToEntity` / `MapToModel` inside `DocumentFileRepository` map them in sync (matching the Entity's Subject/Grade/Year).

## 2. Interface changes

### 2.1 IDocumentFileRepository

```csharp
/// <summary>
/// Update subject/grade/year metadata. Null parameters are ignored (not cleared).
/// </summary>
Task<bool> UpdateMetadataAsync(Guid id, string? subject, string? grade, string? year);
```

### 2.2 IDocumentFileService

```csharp
/// <summary>
/// Update subject/grade/year metadata. Null parameters are ignored (not cleared).
/// Returns the updated model, or null if not found.
/// </summary>
Task<DocumentFileModel?> UpdateMetadataAsync(Guid id, string? subject, string? grade, string? year);
```

### 2.3 IDocumentAnalysisService

```csharp
/// <summary>
/// Maximum text chunk size in characters for LLM calls.
/// Dynamically calculated from model context length at initialization.
/// </summary>
int ChunkSize { get; }

/// <summary>
/// Maximum number of concurrent LLM calls.
/// </summary>
int MaxConcurrency { get; }

/// <summary>
/// Initialize model parameters. Called at startup and lazily on first use.
/// </summary>
Task InitializeAsync(CancellationToken cancellationToken = default);

/// <summary>
/// Analyze document text preview to determine subject, grade, and year metadata.
/// Focused prompt for metadata only (no segmentation strategy).
/// Returns null on failure (caller should treat as best-effort).
/// </summary>
Task<DocumentMetadataAnalysis?> AnalyzeMetadataAsync(string textPreview, CancellationToken cancellationToken = default);
```

### 2.4 ISearchIndexService

```csharp
/// <summary>
/// Update subject/grade/year for all indexed blocks of a document file.
/// Used when metadata is set/updated after initial indexing.
/// </summary>
Task UpdateDocumentFileMetadataAsync(Guid documentFileId, string? subject, string? grade, string? year);
```

### 2.5 HTTP endpoints

```
PUT /admin/document-files/{id}/metadata
Content-Type: application/json

Request (UpdateMetadataRequest DTO):
{
  "subject": "English",   // optional, null/omitted = no change
  "grade": "G10",         // optional, null/omitted = no change
  "year": "2024"          // optional, null/omitted = no change
}

Response 200:
{
  "success": true,
  "data": {
    "id": "...",
    "fileName": "...",
    "subject": "English",
    "grade": "G10",
    "year": "2024",
    "updatedAt": "..."
  }
}

Response 404: { "success": false, "message": "File not found", "errorCode": "DOCTHECA_FILE_NOT_FOUND" }
```

**DTO definition** (`DocumentFileEndpoints.cs`):

```csharp
/// <summary>
/// Request body for PUT /admin/document-files/{id}/metadata.
/// All fields optional — null means "no change", empty string means "clear".
/// </summary>
public record UpdateMetadataRequest
{
    public string? Subject { get; init; }
    public string? Grade { get; init; }
    public string? Year { get; init; }
}
```

**Null semantics**: `null` means "no change"; `""` (empty string) is written to the DB (stored as an actual empty string, not treated as NULL). The current implementation does no MaxLength validation at the endpoint layer; the DB column constraints act as the fallback (subject=50, grade=20, year=10).

## 3. DocumentMetadataAnalysis model

```csharp
namespace Doctheca.Domain.Models;

/// <summary>
/// LLM metadata analysis result. Fields are null if LLM could not determine them.
/// </summary>
public record DocumentMetadataAnalysis
{
    public string? Subject { get; init; }
    public string? Grade { get; init; }
    public string? Year { get; init; }
}
```

> Note: Deserialization uses `JsonNamingPolicy.SnakeCaseLower` + `JsonIgnoreCondition.WhenWritingNull`; response fields that are empty strings / whitespace / the `"null"` literal are all treated as null.
```

## 4. LLM prompt

`BuildMetadataAnalysisPrompt` builds the prompt; the request body is assembled by `BuildRequestBody` (including system + user roles, `max_tokens`, `temperature`, `stream = true`).

> Note: the prompts below are quoted in English translation; the runtime prompt text in `DocumentAnalysisPromptBuilder.cs` and `DocumentAnalysisService.cs` remains Chinese by design (the analyzed documents are Chinese).

**system prompt**: `Return JSON directly, no explanation.`

**user prompt** (`BuildMetadataAnalysisPrompt`, string interpolation):

```
You are a document analysis expert. Analyze the following document content and identify the subject and grade.

Subject types:
- English: English textbooks, reading materials
- Chinese: Chinese language textbooks, classical Chinese, modern Chinese texts
- Math: math textbooks, problem sets
- Physics: physics textbooks, lab reports
- Chemistry: chemistry textbooks, lab reports
- Biology: biology textbooks
- Other: cannot be clearly determined

Grade (inferred from the title or content):
- K: kindergarten/preschool
- G1-G12: grade 1 of elementary school through grade 12 of high school
- Return null when it cannot be determined

Year (inferred from the title, header, copyright page, etc., 4 digits):
- Return null when it cannot be determined

Analyze the following document content and return JSON. Return only JSON, no other text.

```json
{
  "subject": "subject or null",
  "grade": "grade or null",
  "year": "year or null"
}
```

Document content:
---
{{textPreview}}
---
```

## 5. Implementation specifications

### 5.1 DocumentAnalysisService.AnalyzeMetadataAsync

```
1. await InitializeAsync(ct)  // On first call, resolves ContextLength and computes the internal fields _chunkSize / _maxTokensValue; does not modify the injected DocumentAnalysisOptions
2. if textPreview is null/whitespace → return null, log warning "Empty text preview provided for metadata analysis"
3. if ChunkSize <= 0 || MaxTokensValue <= 0 (LLM initialization failed/not configured) → return null, log info
4. truncate textPreview to first 2000 chars
5. DocumentAnalysisPromptBuilder.BuildMetadataAnalysisPrompt(preview)
6. BuildRequestBody(prompt) → system="Return JSON directly, no explanation." + user prompt, stream=true
7. try:
   - CallStreamingAsync(requestBody, ct)
   - DocumentAnalysisResponseParser.ParseMetadataAnalysis(response, _logger, JsonOptions): strip markdown code block wrapping, deserialize, empty string/whitespace/"null" literal → null
   - return result
8. catch any exception:
   - log warning(ex) "LLM metadata analysis failed, returning null"
   - return null
```

### 5.2 StructaDocParseWorker integration

Call the new method at the end of `PersistParseResultAsync` and `PersistMergedChunkResultsAsync` (after the status becomes Parsed and after OpenSearch indexing):

```
AnalyzeMetadataIfMissingAsync(parse.DocumentFileId, result.Markdown, scopeProvider, ct)
```

```
AnalyzeMetadataIfMissingAsync(documentFileId, markdownContent, scopeProvider, ct):
1. resolve IDocumentFileService from scope
2. get document file by id
3. if file not found → log warning "Document file not found for metadata analysis: {FileId}", return
4. if file.Subject/file.Grade/file.Year are all non-empty (!IsNullOrWhitespace) →
     log info "Metadata already set for file {FileId}, skip LLM analysis (Subject={Subject}, Grade={Grade}, Year={Year})", return
5. resolve IDocumentAnalysisService? from scope (GetService, may be null)
6. if llmService is null → log info "LLM not configured, skip metadata analysis for file {FileId}", return
7. if markdownContent is null/whitespace → log info "No markdown content for file {FileId}, skip metadata analysis", return
8. extract first 2000 chars of markdownContent (truncated at the Worker layer; AnalyzeMetadataAsync truncates again as redundant protection)
9. log info "Starting LLM metadata analysis for file {FileId}"
10. try:
    - analysis = await llmService.AnalyzeMetadataAsync(textPreview, ct)
    - if analysis is null → log warning "LLM metadata analysis returned null for file {FileId}", return
    - determine newSubject = file.Subject non-empty ? file.Subject : analysis.Subject
      (i.e., take analysis.Subject when IsNullOrWhitespace(file.Subject), otherwise keep the original value)
    - determine newGrade / newYear the same way
    - if newSubject == file.Subject && newGrade == file.Grade && newYear == file.Year (no change) →
        log info "LLM did not provide any missing metadata for file {FileId}", return
    - await fileService.UpdateMetadataAsync(documentFileId, newSubject, newGrade, newYear)
    - log info "Metadata updated by LLM for file {FileId}: Subject={Subject}, Grade={Grade}, Year={Year}"
    - try: await searchIndexService.UpdateDocumentFileMetadataAsync(documentFileId, newSubject, newGrade, newYear)
      catch(syncEx): log warning "Failed to sync OpenSearch metadata for file {FileId}"
11. catch OperationCanceledException when ct.IsCancellationRequested → rethrow
12. catch any other exception → log warning(ex) "LLM metadata analysis failed for file {FileId}", return (do not throw)
```

### 5.3 DocumentFileEndpoints — PUT /{id}/metadata

```
1. parse request body { subject?, grade?, year? }
2. call fileService.UpdateMetadataAsync(id, subject, grade, year)
3. if returns null → 404
4. try: searchIndexService.UpdateDocumentFileMetadataAsync(id, updated.Subject, updated.Grade, updated.Year)
5. catch: log warning
6. return 200 with updated model
```

### 5.4 OpenSearchIndexService.UpdateDocumentFileMetadataAsync

Uses the OpenSearch `_update_by_query` API to match by `document_file_id` and update subject/grade/year:

```json
{
  "query": { "term": { "document_file_id": "<fileId>" } },
  "script": {
    "source": "ctx._source.subject = params.subject; ctx._source.grade = params.grade; ctx._source.year = params.year;",
    "params": { "subject": "...", "grade": "...", "year": "..." }
  }
}
```

## 6. Error handling

| Scenario | Handling | Code location |
|------|------|---------|
| LLM service not registered (ApiKey empty) | `IDocumentAnalysisService` not registered; the Worker gets null and skips, logging at Info | `Program.cs` conditional DI registration; `StructaDocParseWorker.AnalyzeMetadataIfMissingAsync` |
| LLM not initialized/disabled (ChunkSize<=0) | `AnalyzeMetadataAsync` returns null internally, logging at Info | `DocumentAnalysisService.AnalyzeMetadataAsync:171-175` |
| LLM call timeout/network error | catch → return null; the Worker logs at Warning | `StructaDocParseWorker` |
| LLM returns non-JSON | `ParseMetadataAnalysis` catches JsonException → return null | `DocumentAnalysisService:294-298` |
| LLM returns empty strings/whitespace/"null" literals | Treated as null; only fields with actual values are filled | `DocumentAnalysisService.ParseMetadataAnalysis:277-285` |
| OpenSearch update fails | inner try/catch, logs Warning (syncEx), does not block | `StructaDocParseWorker` |
| Document does not exist | `GetByIdAsync` → null, logs Warning, does not block | `StructaDocParseWorker` |
| HTTP request body parse failure | endpoint layer catch → 400 `"Invalid request body"` | `DocumentFileEndpoints:285-289` |

## 7. Test strategy

### 7.1 Current test status

**No unit tests at present.** `DocumentAnalysisServiceTests.cs` does not exist under `src/Tests/Doctheca.Tests/`; the metadata analysis functionality currently has zero test coverage.

### 7.2 Suggested unit test directions (UT)

The following test directions are designed around pure-logic `internal` testable methods, with no dependency on LLM HTTP or the database:

- `DocumentAnalysisPromptBuilder.BuildMetadataAnalysisPrompt(string textPreview)`
- `DocumentAnalysisResponseParser.ParseMetadataAnalysis(string response, ILogger<DocumentAnalysisService> logger, JsonSerializerOptions jsonOptions)`
- `DocumentAnalysisService.ParseTokenCount(string? value)`

| # | Suggested coverage | Methods involved | Covers |
|---|------------|---------|------|
| UT-DM-01 | Empty/whitespace text → `AnalyzeMetadataAsync` returns null | `AnalyzeMetadataAsync` | FR-04, FR-05 |
| UT-DM-02 | `ChunkSize <= 0` (LLM not initialized) → returns null | `AnalyzeMetadataAsync` | FR-05 |
| UT-DM-03 | Valid JSON response → returns correct subject/grade/year | `DocumentAnalysisResponseParser.ParseMetadataAnalysis` | FR-03, FR-04 |
| UT-DM-04 | JSON fields are empty strings / whitespace → treated as null | `DocumentAnalysisResponseParser.ParseMetadataAnalysis` | FR-04 |
| UT-DM-05 | JSON field is the `"null"` string literal → treated as null | `DocumentAnalysisResponseParser.ParseMetadataAnalysis` | FR-04 |
| UT-DM-06 | Invalid JSON → returns null | `DocumentAnalysisResponseParser.ParseMetadataAnalysis` | FR-05 |
| UT-DM-07 | Long text input → truncated to 2000 characters | `AnalyzeMetadataAsync` | FR-04 |
| UT-DM-08 | `BuildMetadataAnalysisPrompt` contains subject/grade/year instructions | `DocumentAnalysisPromptBuilder.BuildMetadataAnalysisPrompt` | FR-04 |
| UT-DM-09 | `BuildMetadataAnalysisPrompt` contains no segmentation strategy keywords | `DocumentAnalysisPromptBuilder.BuildMetadataAnalysisPrompt` | FR-04 |
| UT-DM-10 | JSON wrapped in a markdown code block parses correctly | `DocumentAnalysisResponseParser.ParseMetadataAnalysis` | FR-04 |
| UT-DM-11 | `"128K"` / `"1M"` / plain numbers → token counts parsed correctly | `DocumentAnalysisService.ParseTokenCount` | Initialization logic |

### 7.3 Suggested integration test directions (IT)

| # | Suggested coverage | Covers |
|---|------------|------|
| IT-DM-01 | After parsing completes, the LLM is not called when all metadata fields have values | AC-03 |
| IT-DM-02 | After parsing completes, the LLM is called to fill missing metadata | AC-04 |
| IT-DM-03 | LLM failure does not affect the parse status | AC-05 |
| IT-DM-04 | Analysis is skipped when the LLM is not configured | AC-06 |
| IT-DM-05 | OpenSearch is refreshed in sync after manually setting metadata | AC-07 |
| IT-DM-06 | The LLM does not overwrite existing metadata | AC-08 |

## 8. Impact scope

| Component | Impact |
|------|------|
| `document_files` table | 3 new columns (nullable) |
| `DocumentFileEndpoints` | New PUT endpoint |
| `StructaDocParseWorker` | Metadata analysis step added after parsing completes |
| `DocumentAnalysisService` | New AnalyzeMetadataAsync method (existing methods unchanged) |
| `OpenSearchIndexService` | New UpdateDocumentFileMetadataAsync method |
| Admin frontend page | Metadata display/edit can be added later (not implemented in this iteration) |
| Deployment scripts | No change (reuses the existing LLM configuration) |

## 9. Deployment scripts

**Not modified.** The existing `LLM_API_KEY` / `LLM_BASE_URL` / `LLM_MODEL` configuration in `start.sh` is kept; `AnalyzeMetadataAsync` reuses the same `LlmDocumentAnalysis` configuration.
