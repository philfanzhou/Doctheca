# DocumentMetadataAnalysis — Naming and style conventions

## Naming conventions

| Kind | Convention | Example |
|------|------|------|
| Interface | `I` prefix + PascalCase | `IDocumentAnalysisService` |
| Implementation class | PascalCase, no suffix | `DocumentAnalysisService` |
| Record (DTO) | PascalCase | `DocumentMetadataAnalysis` |
| Constant | PascalCase | `SubjectEnglish` |
| Method | PascalCase, Async suffix | `AnalyzeMetadataAsync` |
| Parameter | camelCase | `textPreview` |

## Logging conventions

- Success/informational: `LogInformation`, including fileId/parseId
- Degraded/recoverable: `LogWarning`, including the exception object
- Unexpected errors: `LogError`, including the exception object
- LLM key masking: use `SensitiveDataMasker.MaskApiKey()`

Key log messages:
```
"Metadata already set for file {FileId}, skip LLM analysis"
"LLM not configured, skip metadata analysis for file {FileId}"
"No markdown content for file {FileId}, skip metadata analysis"
"Starting LLM metadata analysis for file {FileId}"
"Metadata updated by LLM for file {FileId}: Subject={Subject}, Grade={Grade}, Year={Year}"
"Failed to sync OpenSearch metadata for file {FileId}"
```

## Error message conventions

Error messages use Chinese (end-user facing), while internal exception messages in code use English:
- `"LLM metadata analysis failed"` — internal exception message
- HTTP response messages are controlled by the endpoint layer

## Null semantics

| Value | Semantics |
|----|------|
| `null` | No change (UpdateMetadataAsync parameters) |
| `""` (empty string) | Clear the field |
| `DocumentMetadataAnalysis` field is null | The LLM could not determine the field |

## LLM configuration conventions

- Configuration section: `LlmDocumentAnalysis` (`DocumentAnalysisOptions.SectionName`)
- `IDocumentAnalysisService` is only registered when the configuration section exists and `ApiKey` is non-empty (conditional DI in `Program.cs`); when not registered, the Worker gets null via `GetService` and then skips
- Temperature fixed at 0.1 (`DocumentAnalysisOptions.Temperature` read-only property, structured output)
- Streaming SSE calls, stream idle timeout defaults to 60s (`StreamIdleTimeoutSeconds`)
- ContextLength supports human-friendly formats such as `128K` / `1M` / plain numbers; when not configured it is fetched dynamically via GET `/v1/models/{Model}`, and if that also fails the LLM is disabled

## Null validation boundary

> This feature **does not enforce validation** of the value ranges for subject/grade/year. `DocthecaConstants.ValidSubjects` / `ValidGrades` serve only as LLM prompt examples and a reserve for future validation; `UpdateMetadataAsync` and the Entity column constraints only provide `MaxLength` + nullable fallbacks.
