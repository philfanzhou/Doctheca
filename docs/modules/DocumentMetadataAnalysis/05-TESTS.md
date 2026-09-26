# DocumentMetadataAnalysis — Test documentation

## Current test status

**No unit test coverage at present.** `DocumentAnalysisServiceTests.cs` does not exist under `src/Tests/Doctheca.Tests/`; the metadata analysis functionality currently has zero tests. Project tests are concentrated in files such as `ConstantsTests` / `DocumentFileServiceTests` / `DocumentFileDeleteCleanupTests` / `DocumentParseBlockServiceTests` / `OpenSearchIndexServiceTests`; see the repository test directory for details.

> This repository uses xUnit + Moq + FluentAssertions. New tests must follow the test conventions for the `doctheca` project in `.agent/rules/coding-policy.md`.

## Suggested coverage directions

The following are suggested coverage directions based on the `internal` testable methods in `DocumentAnalysisService` (require `InternalsVisibleTo` for the test project); all are pure-logic tests with no dependency on LLM HTTP or the database.

### Unit test directions (suggested access to internal methods via `InternalsVisibleTo`)

| # | Coverage direction | Target method (test entry) | Covers FR |
|---|---------|-------------------|---------|
| T-01 | `textPreview` is null/whitespace → `AnalyzeMetadataAsync` returns null | `AnalyzeMetadataAsync` | FR-04, FR-05 |
| T-02 | `ChunkSize <= 0` (LLM not initialized/disabled) → returns null, no LLM call | `AnalyzeMetadataAsync` | FR-05 |
| T-03 | Valid JSON response → correct subject/grade/year | `ParseMetadataAnalysis` | FR-03, FR-04 |
| T-04 | JSON field is an empty string → treated as null | `ParseMetadataAnalysis` | FR-04 |
| T-05 | JSON field is whitespace → treated as null | `ParseMetadataAnalysis` | FR-04 |
| T-06 | JSON field is the `"null"` string literal → treated as null | `ParseMetadataAnalysis` | FR-04 |
| T-07 | Invalid JSON → returns null without throwing | `ParseMetadataAnalysis` | FR-05 |
| T-08 | JSON wrapped in markdown ```json``` / ```` ``` code blocks parses correctly | `ParseMetadataAnalysis` (`ExtractJson`) | FR-04 |
| T-09 | Long text (>2000 characters) input → truncated to the first 2000 characters | `AnalyzeMetadataAsync` | FR-04 |
| T-10 | `BuildMetadataAnalysisPrompt` contains subject/grade/year instruction keywords | `BuildMetadataAnalysisPrompt` | FR-04 |
| T-11 | `BuildMetadataAnalysisPrompt` contains no segmentation/chunking strategy terms | `BuildMetadataAnalysisPrompt` | FR-04 |
| T-12 | `ParseTokenCount("128K")` → 131072, `"1M"` → 1048576, plain numbers → same value | `ParseTokenCount` | Initialization logic |

### Integration test directions (planned)

| # | Coverage direction | Components involved | Covers AC |
|---|---------|---------|---------|
| IT-01 | After parsing completes, all metadata fields have values → Worker calls `GetService<IDocumentAnalysisService>` then skips the LLM | `StructaDocParseWorker` + `IDocumentFileService` | AC-03 |
| IT-02 | After parsing completes, metadata is missing → LLM called to fill it, DB updated, OpenSearch synced | `StructaDocParseWorker` + `DocumentAnalysisService` (mock) | AC-04 |
| IT-03 | LLM call fails → parse status remains `parsed`, OpenSearch index already written | `StructaDocParseWorker` | AC-05 |
| IT-04 | `IDocumentAnalysisService` not registered (ApiKey empty) → Worker skips, logs at Info | `StructaDocParseWorker` (no DI registration) | AC-06 |
| IT-05 | Manual PUT metadata → DB update + OpenSearch `UpdateByQueryAsync` sync | `DocumentFileEndpoints` + `OpenSearchIndexService` | AC-07 |
| IT-06 | LLM analysis does not overwrite existing fields (keeps original DB values) | `StructaDocParseWorker.AnalyzeMetadataIfMissingAsync` | AC-08 |

## How to run

```bash
# Run all tests (no dedicated DocumentAnalysis tests at present)
dotnet test src/Tests/Doctheca.Tests --configuration Release

# Filter by existing test file (example)
dotnet test src/Tests/Doctheca.Tests --filter "FullyQualifiedName~OpenSearchIndexServiceTests"
```

## Coverage targets (pending DM-18)

- `DocumentAnalysisService` line coverage ≥ 80%
- Pure-logic methods `BuildMetadataAnalysisPrompt` / `ParseMetadataAnalysis` / `ParseTokenCount` at 100% coverage
