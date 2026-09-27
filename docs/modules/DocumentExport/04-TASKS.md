# DocumentExport — Task List

> **All tasks in this module are completed.** The following is a historical record.

## Completed Tasks

| ID | Task | Status |
|----|------|------|
| DE-01 | Create `MarkdownExportHelper` (ReplaceImagePathsRelative / ReplaceImagePathsBase64Async / ReplaceImagePathsPresignedAsync / BuildMarkdownZipAsync / BuildHtmlStream) | completed |
| DE-02 | Create `DocumentExportEndpoints` (ExportMarkdown / ExportHtml / ExportParseMarkdown / ExportParseHtml + MapDocumentExportEndpoints) | completed |
| DE-03 | Implement `ExportMarkdownCore` (shared Markdown/ZIP core) | completed |
| DE-04 | Implement `ExportHtmlCore` (shared HTML core) | completed |
| DE-05 | Implement file-level endpoint precondition validation (file exists / parsed) | completed |
| DE-06 | Implement parse-level endpoint precondition validation (parse record exists / parsed / file name fallback) | completed |
| DE-07 | Register `MapDocumentExportEndpoints` in Program.cs | completed |
| DE-08 | Unit tests `DocumentExportLogicTests` (12 tests) | completed |

## Command Quick Reference

```bash
# Build
dotnet build Doctheca.sln --configuration Release

# Test (this module)
dotnet test src/Tests/Doctheca.Tests --filter "FullyQualifiedName~DocumentExport"

# Test (all)
dotnet test src/Tests/Doctheca.Tests
```
