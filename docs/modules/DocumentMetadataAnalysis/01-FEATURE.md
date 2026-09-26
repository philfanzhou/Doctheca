# 01-FEATURE — Document Metadata Analysis

## Feature overview

After parsing completes, the LLM is automatically called to analyze the first 2000 characters of the document's Markdown content, identify the subject and grade, and backfill the results into the `document_files` table. If a document's subject/grade/year all already have values (entered manually or from a previous LLM analysis), the LLM call is skipped. An LLM failure does not affect the parsing flow.

## Background

The `document_files` table currently has no subject/grade/year metadata fields. These fields stay empty during OpenSearch indexing, making it impossible to filter searches by subject/grade. This feature:

1. Adds subject/grade/year columns (nullable) to the `document_files` table
2. Provides an API endpoint to set metadata manually
3. Automatically calls the LLM to fill in missing metadata after parsing completes
4. Refreshes the OpenSearch index in sync after metadata updates

## User stories

- **As a teacher**: after I upload handouts, the system automatically identifies the subject and grade — no manual entry needed
- **As a teacher**: I can specify the subject/grade manually before or after parsing, and the system will not override my choice
- **As an operator**: when the LLM is unavailable, the parsing flow still completes normally and only the metadata stays empty
- **As an operator**: when a document is parsed multiple times, the LLM is not called again if the metadata already has values

## Functional requirements

### FR-01: New metadata fields on document_files
- Add three columns: `subject` (string, nullable, max 50), `grade` (string, nullable, max 20), `year` (string, nullable, max 10)
- These fields stay empty at upload time (the upload API does not carry metadata)
- Set manually via the new PUT endpoint

### FR-02: Manual metadata endpoint
- `PUT /admin/document-files/{id}/metadata`
- Request body: `{ "subject": "...", "grade": "...", "year": "..." }` (all three fields optional; null means no change)
- Response: the updated document information
- After manual setting, subsequent parse completions skip LLM analysis (as long as all three fields have values)

### FR-03: Automatic LLM analysis after parsing completes
- After the parse status becomes `parsed`, check subject/grade/year in `document_files`
- If all three fields have values → skip the LLM
- If any field is empty → call the LLM to analyze the first 2000 characters of the Markdown
- After the LLM returns, only update the fields that are empty (never overwrite existing values)
- Update subject/grade/year in sync for all blocks of this document_file in the OpenSearch index

### FR-04: LLM analysis input
- Input: the first 2000 characters of the parse result `MarkdownContent` (human-readable Markdown body, not JSON)
- The prompt focuses on subject and grade identification (does not ask for segmentation strategy, document type, etc.)
- The LLM returns JSON: `{ "subject": "...", "grade": "...", "year": "..." }`

### FR-05: LLM failure does not block
- LLM service not configured (ApiKey empty) → skip, log at Info level
- LLM call fails (network/timeout/parse error) → skip, log at Warning level
- The parse status is unaffected (remains `parsed`)
- The OpenSearch index is unaffected (blocks remain indexed, only subject/grade/year are empty)

### FR-06: OpenSearch index sync
- After metadata updates (automatic by LLM or manual), call `UpdateDocumentFileMetadataAsync` to refresh OpenSearch
- Match by `document_file_id` and update the subject/grade/year fields of all blocks
- Failures only log a Warning and do not block

## Acceptance criteria

| AC | Description |
|----|------|
| AC-01 | The `document_files` table has subject/grade/year columns, nullable |
| AC-02 | `PUT /admin/document-files/{id}/metadata` can update metadata |
| AC-03 | After parsing completes, the LLM is not called if all metadata fields have values |
| AC-04 | After parsing completes, the LLM is called to fill the missing fields if metadata is partially missing |
| AC-05 | LLM failure does not affect the parse status or the OpenSearch index |
| AC-06 | When the LLM is not configured, analysis is skipped without error |
| AC-07 | After metadata updates, the OpenSearch index is refreshed in sync |
| AC-08 | Manually set metadata is not overwritten by subsequent LLM analysis |

## Non-functional requirements

| NFR | Description |
|-----|------|
| NFR-01 | LLM analysis runs asynchronously and does not block the parse status update |
| NFR-02 | LLM analysis failure only logs and does not affect any downstream flow |
| NFR-03 | LLM calls use the existing `LlmDocumentAnalysis` configuration (BaseUrl/ApiKey/Model) |
| NFR-04 | LLM input is human-readable Markdown (not JSON), first 2000 characters |
| NFR-05 | Logs record fileId/parseId/analysis result/failure reason, with the ApiKey masked |

## Data sources

- **LLM input**: first 2000 characters of `document_parses.markdown_content`
- **Metadata storage**: `document_files.subject` / `grade` / `year`
- **LLM configuration**: `LlmDocumentAnalysis` configuration section (reuses the existing configuration, nothing new)

## Interface inventory

| Component | Change |
|------|------|
| `DocumentFileEntity` | Add Subject/Grade/Year columns |
| `DocumentFileModel` | Add Subject/Grade/Year fields |
| `IDocumentFileRepository` | Add `UpdateMetadataAsync` |
| `IDocumentFileService` | Add `UpdateMetadataAsync` |
| `IDocumentAnalysisService` | Add `AnalyzeMetadataAsync` method |
| `ISearchIndexService` | Add `UpdateDocumentFileMetadataAsync` method |
| `StructaDocParseWorker` | Call LLM metadata analysis after parsing completes |
| `DocumentFileEndpoints` | Add `PUT /{id}/metadata` endpoint |
