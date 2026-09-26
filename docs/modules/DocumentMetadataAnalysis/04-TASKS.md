# DocumentMetadataAnalysis — Task list

> **Implementation tasks for this module are complete, but unit tests have not been written yet.** See 05-TESTS for details.

## Completed tasks

| ID | Task | Status |
|----|------|------|
| DM-01 | Create DocumentMetadataAnalysis record (Domain/Models) | completed |
| DM-02 | Create DocumentAnalysisOptions (inherits AiClientOptions) | completed |
| DM-03 | Implement DocumentAnalysisService.AnalyzeMetadataAsync | completed |
| DM-04 | Create IDocumentAnalysisService interface | completed |
| DM-05 | Create DocthecaConstants (ValidSubjects / ValidGrades) | completed |
| DM-06 | Add Subject/Grade/Year columns to DocumentFileEntity | completed |
| DM-07 | Add Subject/Grade/Year fields to DocumentFileModel | completed |
| DM-08 | Add UpdateMetadataAsync to IDocumentFileRepository | completed |
| DM-09 | Add UpdateMetadataAsync to IDocumentFileService | completed |
| DM-10 | Implement IDocumentFileRepository.UpdateMetadataAsync | completed |
| DM-11 | Implement DocumentFileService.UpdateMetadataAsync | completed |
| DM-12 | Add UpdateDocumentFileMetadataAsync to ISearchIndexService | completed |
| DM-13 | Implement OpenSearchIndexService.UpdateDocumentFileMetadataAsync | completed |
| DM-14 | Add PUT /{id}/metadata to DocumentFileEndpoints | completed |
| DM-15 | Implement MinerUFileParseWorker.AnalyzeMetadataIfMissingAsync | completed |
| DM-16 | DI registration of IDocumentAnalysisService (conditional registration) | completed |
| DM-17 | Program.cs LLM initialization calls InitializeAsync | completed |

## Pending tasks

| ID | Task | Status | Notes |
|----|------|------|------|
| DM-18 | Unit tests for DocumentAnalysisService (covering internal methods) | pending | Suggested directions in 02-SPEC §7.2; currently zero test coverage |

## Command quick reference

```bash
# Build
dotnet build Doctheca.sln --configuration Release

# Test (no DocumentAnalysis tests available to filter at present)
dotnet test src/Tests/Doctheca.Tests --configuration Release

# Frontend build
cd frontend && npm run build
```
