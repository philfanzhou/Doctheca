# DocumentExport — Test Documentation

## Unit Tests (DocumentExportLogicTests.cs)

12 pure-logic unit tests in total, located in `DocumentExportLogicTests.cs`. The tests cover image path replacement, Markdig conversion, the HTML template, ZIP structure, and Base64 round trips, with no dependency on HTTP / OSS / database.

| # | Test method | Coverage | Status |
|---|---------|------|------|
| UT-DE-01 | `MarkdownPathReplacement_ReplacesS3PathWithRelativePath` | FR-05 (basic relative path replacement) | completed |
| UT-DE-02 | `MarkdownPathReplacement_ReplacesSrcAttribute` | FR-05 (double-quoted src attribute replacement) | completed |
| UT-DE-03 | `MarkdownPathReplacement_ReplacesSrcAttribute_SingleQuotes` | FR-05 (single-quoted src attribute replacement) | completed |
| UT-DE-04 | `MarkdownPathReplacement_ReplacesMixedQuoteFormats` | FR-05 (mixed formats + no leftover absolute paths) | completed |
| UT-DE-05 | `MarkdownPathReplacement_HandlesMultipleImages` | FR-05 (batch replacement of multiple images) | completed |
| UT-DE-06 | `MarkdownPathReplacement_DoesNotReplaceIfPathNotFound` | FR-05 (unchanged when the path does not match) | completed |
| UT-DE-07 | `HtmlExport_ReplacesS3PathWithDataUri` | FR-05 (Base64 data URI replacement) | completed |
| UT-DE-08 | `Markdig_ConvertsMarkdownToHtml` | FR-02 (Markdown→HTML basics: headings/bold/lists) | completed |
| UT-DE-09 | `Markdig_ConvertsMarkdownWithImageToHtml` | FR-02 (Markdown→HTML images: img/alt/src) | completed |
| UT-DE-10 | `HtmlTemplate_GeneratesValidHtmlDocument` | FR-02 (self-contained HTML document structure) | completed |
| UT-DE-11 | `ZipExport_ContainsMarkdownAndImages` | FR-01 (ZIP contains .md and images/) | completed |
| UT-DE-12 | `Base64Conversion_RoundTripsCorrectly` | FR-05 (Base64 encode/decode round trip + data URI prefix) | completed |

### FR/AC Mapping

- FR-01 (Markdown/ZIP export) → UT-DE-11
- FR-02 (HTML export) → UT-DE-08, UT-DE-09, UT-DE-10
- FR-03 (parse-level Markdown export) → shares `ExportMarkdownCore` with FR-01, same as UT-DE-11
- FR-04 (parse-level HTML export) → shares `ExportHtmlCore` with FR-02, same as UT-DE-08/09/10
- FR-05 (three image path modes) → UT-DE-01 through UT-DE-07, UT-DE-12
- FR-06 / FR-07 (validation and fault tolerance) → not covered (requires integration tests)

## Integration Tests (not implemented)

The endpoint layer (`DocumentExportEndpoints`) and the shared cores (`ExportMarkdownCore` / `ExportHtmlCore`) currently have no integration tests; they need to be added to cover FR-06 / FR-07 and AC-04 through AC-09.

| # | Test case | Coverage |
|---|---------|------|
| IT-DE-01 | File-level Markdown export returns a valid ZIP whose entries include .md and images/ | AC-01, AC-08 |
| IT-DE-02 | File-level HTML export returns HTML containing Base64 img | AC-02, AC-09 |
| IT-DE-03 | Parse-level export matches file-level export (same parseId) | AC-03 |
| IT-DE-04 | Missing file returns 404 + DOCTHECA_FILE_NOT_FOUND | AC-04 |
| IT-DE-05 | Missing parse record returns 404 + DOCTHECA_PARSE_NOT_FOUND | AC-05 |
| IT-DE-06 | Not parsed returns 422 + DOCTHECA_FILE_NOT_PARSED / DOCTHECA_PARSE_NOT_PARSED | AC-06 |
| IT-DE-07 | A single image OSS download failure does not block the export; a Warning is logged | AC-07 |

## How to Run

```bash
# Run this module's unit tests
dotnet test src/Tests/Doctheca.Tests --filter "FullyQualifiedName~DocumentExport"

# Run all tests
dotnet test src/Tests/Doctheca.Tests
```

## Coverage Targets

- `MarkdownExportHelper` line coverage ≥ 80%
- For the `DocumentExportEndpoints` endpoint layer, integration tests covering the precondition validation branches are recommended
