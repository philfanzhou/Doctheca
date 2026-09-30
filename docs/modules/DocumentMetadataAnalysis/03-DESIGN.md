# DocumentMetadataAnalysis — Design notes

## Directory and file structure of this feature in the project

```

├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── DocumentMetadataAnalysis.cs        # LLM analysis result record (Subject/Grade/Year)
│   │   │   ├── DocumentAnalysisOptions.cs          # LLM configuration options (inherits AiClientOptions)
│   │   │   ├── DocumentFileModel.cs                # File model (includes Subject/Grade/Year)
│   │   │   └── DocthecaConstants.cs              # Subject/grade constants (ValidSubjects/ValidGrades, not enforced by this feature)
│   │   ├── Services/
│   │   │   ├── IDocumentAnalysisService.cs         # LLM analysis interface
│   │   │   ├── IDocumentFileService.cs             # File service interface (includes UpdateMetadataAsync)
│   │   │   ├── DocumentFileService.cs              # File service implementation
│   │   │   └── ISearchDomainService.cs             # Search domain service
│   │   └── Repositories/
│   │       ├── IDocumentFileRepository.cs           # File repository interface (includes UpdateMetadataAsync)
│   │       ├── ISearchIndexService.cs               # Search index interface (includes UpdateDocumentFileMetadataAsync)
│   │       └── (DocumentFileRepository.cs)           # File repository implementation (private MapToEntity/MapToModel)
│   ├── Service/
│   │   ├── DocumentAnalysisService.cs               # LLM analysis implementation (OpenAiCompatibleClient streaming SSE)
│   │   ├── Analysis/
│   │   │   ├── DocumentAnalysisPromptBuilder.cs     # prompt building (pure static logic)
│   │   │   └── DocumentAnalysisResponseParser.cs    # response parsing (includes response records, ExtractJson)
│   │   ├── StructaDocParseWorker.cs                 # background parse Worker (includes AnalyzeMetadataIfMissingAsync)
│   │   ├── OpenSearchIndexService.cs                # OpenSearch index service (includes UpdateDocumentFileMetadataAsync)
│   │   └── Endpoints/
│   │       └── DocumentFileEndpoints.cs             # PUT /{id}/metadata endpoint + UpdateMetadataRequest record
│   ├── Database/
│   │   ├── DatabaseInitializer.cs                   # SQL-based initialization (CREATE TABLE IF NOT EXISTS / ADD COLUMN IF NOT EXISTS)
│   │   ├── DocthecaDbContext.cs                   # EF Core DbContext (Fluent API)
│   │   └── Entities/
│   │       └── DocumentFileEntity.cs                 # includes Subject/Grade/Year columns
│   └── Host/
│       ├── Program.cs                               # DI registration + startup initialization
│       └── appsettings.json                         # LlmDocumentAnalysis configuration section
└── docs/modules/DocumentMetadataAnalysis/
    ├── 01-FEATURE.md
    ├── 02-SPEC.md
    ├── 03-DESIGN.md  (this document)
    ├── 04-TASKS.md
    ├── 05-TESTS.md
    └── 06-CONVENTIONS.md
```

> Historical record: `src/Database/DatabaseInitializer.cs` shown above has been retired. Since 2026-09-30 the schema has been managed by the EF Core migration baseline (`20260930161548_InitialCreate`) with `DocthecaMigrationExecutor` takeover rules; see [Migration History](../../database/migrations.md).

## Key interface signatures

### IDocumentAnalysisService

```csharp
Task<DocumentMetadataAnalysis?> AnalyzeMetadataAsync(string textPreview, CancellationToken cancellationToken = default);
```

### IDocumentFileService (new method)

```csharp
Task<DocumentFileModel?> UpdateMetadataAsync(Guid id, string? subject, string? grade, string? year);
```

### ISearchIndexService (new method)

```csharp
Task UpdateDocumentFileMetadataAsync(Guid documentFileId, string? subject, string? grade, string? year);
```

### HTTP endpoints

```
PUT /admin/document-files/{id}/metadata
Content-Type: application/json

Request: { "subject": "English", "grade": "G10", "year": "2024" }  // all optional
Response 200: { "id": "...", "fileName": "...", "subject": "English", "grade": "G10", "year": "2024", ... }
Response 404: { "success": false, "message": "File not found", "errorCode": "DOCTHECA_FILE_NOT_FOUND" }
```

## Data flow

### PUT /{id}/metadata manual update flow

1. The HTTP layer receives the request and parses the `UpdateMetadataRequest` body
2. Calls `DocumentFileService.UpdateMetadataAsync(id, subject, grade, year)`
3. The Repository layer only updates non-null fields (null = no change)
4. Best-effort: calls `ISearchIndexService.UpdateDocumentFileMetadataAsync` to refresh the OpenSearch index
5. Returns the updated document information

### Automatic LLM analysis flow after parsing completes

1. `StructaDocParseWorker` calls `AnalyzeMetadataIfMissingAsync` in the `succeeded` branch of `PollAsync` after result sync completes — not called when parsing fails or is cancelled
2. Checks subject/grade/year in `document_files`:
   - All satisfy `!IsNullOrWhitespace` → skip the LLM (Info log)
   - `IDocumentAnalysisService` not registered (`GetService` → null) → skip (Info log)
   - Markdown is null/whitespace → skip
3. Extracts the first 2000 characters of the Markdown (truncated at the Worker layer; truncated again inside the service as redundant protection) and calls `DocumentAnalysisService.AnalyzeMetadataAsync`
4. The LLM returns a `DocumentMetadataAnalysis` record (fields may be null)
5. Only fills fields that are currently empty/whitespace (takes `analysis.Subject` when `IsNullOrWhitespace(file.Subject)`); existing values are never overwritten
6. Skips the DB write when there is no actual change (avoids empty transactions)
7. Calls `DocumentFileService.UpdateMetadataAsync(id, newSubject, newGrade, newYear)` to update the DB
8. Best-effort: inner try/catch calls `ISearchIndexService.UpdateDocumentFileMetadataAsync` to refresh OpenSearch (failures only log a Warning)

## LLM call details

### DocumentAnalysisService.AnalyzeMetadataAsync

- **Initialization**: `InitializeAsync` (called at startup + lazily on the first `AnalyzeMetadataAsync`). Resolves the `ContextLength` configuration (supports `"128K"` / `"1M"` / plain numbers); if not configured, calls `TryFetchContextLengthAsync` (GET `/v1/models/{Model}`) to fetch it dynamically. Computes the internal fields from context: `_chunkSize` (`(contextLength - 200 - _maxTokensValue) × 0.8 × 1.5`, capped at `2500`) and `_maxTokensValue` (default `4096`). Disables the LLM when no context is available (`_chunkSize = _maxTokensValue = 0`). The service no longer modifies the injected `DocumentAnalysisOptions` POCO.
- **Input truncation**: takes the first 2000 characters of the Markdown (`textPreview[..2000]`). Note: `StructaDocParseWorker.AnalyzeMetadataIfMissingAsync` already truncates once before the call; this is redundant protection.
- **Request body**: assembled by `BuildRequestBody` — system prompt `"Return JSON directly, no explanation."` + user prompt (`DocumentAnalysisPromptBuilder.BuildMetadataAnalysisPrompt`), `max_tokens = _maxTokensValue`, `temperature = 0.1` (read-only property), `stream = true`.
- **Streaming SSE call**: via the shared `OpenAiCompatibleClient.CallStreamingAsync` (`Doctheca.Ai` namespace, backed by `OpenAiSseReader`); per-attempt timeout + SSE idle timeout (`StreamIdleTimeoutSeconds`, default 60s) are guaranteed by the client.
- **Response parsing**: `DocumentAnalysisResponseParser.ParseMetadataAnalysis` — `DocumentAnalysisResponseParser.ExtractJson` strips ```json``` / ```` ``` wrapping; `JsonSerializer.Deserialize` uses `SnakeCaseLower` + `WhenWritingNull`; empty strings/whitespace/`"null"` literals are all treated as null.
- **Failure handling**: any exception is caught, `LogWarning` is emitted, and null is returned; the Worker layer treats it as best-effort.

### Key internal test entry points

The following `internal` methods are pure-logic test entry points (require `InternalsVisibleTo` for the test project):

- `DocumentAnalysisPromptBuilder.BuildMetadataAnalysisPrompt(string textPreview)` — static, returns the user prompt
- `DocumentAnalysisResponseParser.ParseMetadataAnalysis(string response, ILogger<DocumentAnalysisService> logger, JsonSerializerOptions jsonOptions)` — parses the raw LLM response
- `DocumentAnalysisService.ParseTokenCount(string? value)` — parses `"128K"` / `"1M"` / plain numbers

## Error handling

| Scenario | Handling |
|------|------|
| LLM service not registered | Skip analysis, log at Info |
| LLM call timeout/network error | Skip analysis, log at Warning |
| LLM returns non-JSON | Skip analysis, log at Warning |
| LLM returns null fields | Only update non-null fields |
| OpenSearch update fails | Log Warning, do not block |
| Document does not exist | Log Warning, do not block |

## External dependencies

| Interface | Capability provided | Module |
|------|---------|---------|
| `IDocumentAnalysisService` | LLM metadata analysis | `Doctheca.Service` |
| `ISearchIndexService` | OpenSearch metadata sync | `Doctheca.Service` |
| `IDocumentFileService` | File metadata CRUD | `Doctheca.Domain.Services` |

## DI registration

```csharp
// Program.cs — the LLM service is only registered when the LlmDocumentAnalysis configuration section exists and ApiKey is non-empty
var llmSection = builder.Configuration.GetSection(DocumentAnalysisOptions.SectionName);
if (llmSection.Exists() && !string.IsNullOrEmpty(llmSection["ApiKey"]))
{
    builder.Services.Configure<DocumentAnalysisOptions>(llmSection);
    builder.Services.AddHttpClient(nameof(OpenAiCompatibleClient));
    builder.Services.AddSingleton<OpenAiCompatibleClient>(sp => /* named HttpClient + DocumentAnalysisOptions + StreamIdleTimeoutSeconds */);
    builder.Services.AddTransient<IDocumentAnalysisService, DocumentAnalysisService>();
}
// IDocumentFileService / ISearchIndexService / ISearchDomainService are always registered
// ISearchIndexService → OpenSearchIndexService (Singleton)
// IDocumentFileService → DocumentFileService (Scoped)
```

> `IDocumentAnalysisService` uses `AddTransient`. The Worker resolves it via `GetService<IDocumentAnalysisService>()` (nullable); when not registered, it returns null and then skips.
