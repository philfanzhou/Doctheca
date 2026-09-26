# 03-DESIGN — DocumentExport Design Notes

## Directory and File Structure of This Feature in the Project

```

├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── DocumentParseModel.cs              # Parse record (MarkdownContent / Id / DocumentFileId)
│   │   │   ├── DocumentParseImageModel.cs         # Image model (ImageName / ImagePath / ContentType)
│   │   │   ├── DocumentParseStatus.cs             # Status constants (Pending / Parsing / Parsed / Failed)
│   │   │   └── DocumentFileModel.cs               # File model (FileName)
│   │   └── Services/
│   │       ├── IDocumentParseService.cs           # Parse service interface
│   │       ├── DocumentParseService.cs            # Parse service implementation
│   │       ├── IDocumentFileService.cs            # File service interface
│   │       └── DocumentFileService.cs             # File service implementation
│   ├── Service/
│   │   ├── MarkdownExportHelper.cs                # Core export logic (image path handling / ZIP / HTML)
│   │   └── Endpoints/
│   │       └── DocumentExportEndpoints.cs         # 4 export endpoints
│   └── Host/
│       └── Program.cs                             # Registers MapDocumentExportEndpoints
└── docs/modules/DocumentExport/
    ├── 01-FEATURE.md
    ├── 02-SPEC.md
    ├── 03-DESIGN.md  (this document)
    ├── 04-TASKS.md
    ├── 05-TESTS.md
    └── 06-CONVENTIONS.md
```

## Key Interface Signatures

### IDocumentParseService (reused; methods involved in export)

```csharp
Task<DocumentParseModel?> GetByIdAsync(Guid id);
Task<DocumentParseModel?> GetLatestByFileIdAsync(Guid documentFileId);
Task<List<DocumentParseImageModel>> GetImagesByParseIdAsync(Guid parseId);
```

### IDocumentFileService (reused)

```csharp
Task<DocumentFileModel?> GetByIdAsync(Guid id);
```

### IOssService (reused, vendored Doctheca.Common)

```csharp
Task<Stream> DownloadAsync(string objectPath);
Task<string> GetPresignedUrlAsync(string objectPath, int expirySeconds = 3600);
```

### MarkdownExportHelper (new, internal static)

```csharp
public static string ReplaceImagePathsRelative(string markdown, IReadOnlyList<DocumentParseImageModel> images);
public static Task<string> ReplaceImagePathsBase64Async(string markdown, IReadOnlyList<DocumentParseImageModel> images, IOssService ossService, ILogger logger);
public static Task<string> ReplaceImagePathsPresignedAsync(string markdown, IReadOnlyList<DocumentParseImageModel> images, IOssService ossService, ILogger logger);
public static Task<MemoryStream> BuildMarkdownZipAsync(string fileName, string markdownContent, IReadOnlyList<DocumentParseImageModel> images, IOssService ossService, ILogger logger);
public static MemoryStream BuildHtmlStream(string fileName, string markdownContent);
```

### HTTP Endpoints

```
GET /admin/document-files/{id}/export/markdown      → ZIP (relative paths)
GET /admin/document-files/{id}/export/html          → HTML (Base64 inline)
GET /admin/document-parses/{parseId}/export/markdown → ZIP (relative paths)
GET /admin/document-parses/{parseId}/export/html     → HTML (Base64 inline)
```

## Data Flow

### File-level Markdown export (ZIP)

```
HTTP Request
  → DocumentExportEndpoints.ExportMarkdown
  → fileService.GetByIdAsync(id)                    [validate file exists]
  → parseService.GetLatestByFileIdAsync(id)         [validate parsed]
  → parseService.GetImagesByParseIdAsync(parse.Id)  [fetch image list]
  → MarkdownExportHelper.ReplaceImagePathsRelative  [rewrite paths synchronously]
  → MarkdownExportHelper.BuildMarkdownZipAsync      [download images + package ZIP]
  → Results.Stream(application/zip, "{name}_markdown.zip")
```

### File-level HTML export

```
HTTP Request
  → DocumentExportEndpoints.ExportHtml
  → fileService.GetByIdAsync(id)                    [validate file exists]
  → parseService.GetLatestByFileIdAsync(id)         [validate parsed]
  → parseService.GetImagesByParseIdAsync(parse.Id)  [fetch image list]
  → MarkdownExportHelper.ReplaceImagePathsBase64Async [download images + Base64 inline]
  → MarkdownExportHelper.BuildHtmlStream            [Markdig to HTML + template]
  → Results.Stream(text/html, "{name}.html")
```

### Parse-level export

Shares `ExportMarkdownCore` / `ExportHtmlCore` with the file level; only the entry validation differs:
- Fetches the parse record directly by `parseId`
- The file name is looked up from `document_files`; if missing, it falls back to `"document"`

## Design Decisions

### Why three image path modes are supported

| Mode | Applicable scenario | Rationale |
|------|---------|---------|
| Relative paths | Markdown/ZIP export | Works offline; the `images/` directory has a fixed relative position to the `.md` inside the ZIP |
| Base64 inline | HTML export | Single self-contained file; opens directly in a browser with no external dependencies |
| Presigned URL | In-browser preview (reserved) | Avoids Base64 bloat and saves bandwidth for large images; not currently wired into any endpoint |

All three modes share the `ImagePath` / `ImageName` / `ImageType` fields of `DocumentParseImageModel`; differences are isolated in helper methods, so the endpoint layer does not need to care about image handling details.

### HTML conversion approach

- Chose **Markdig** (already a project dependency); `Markdown.ToHtml` converts in one step
- Wrapped in a complete HTML5 document (`<!DOCTYPE html>` + `lang="zh-CN"` + inline CSS) so it can be read directly when opened in a browser
- Inline CSS covers common elements (headings/paragraphs/lists/tables/quotes/code/images); no external style library is introduced

### Why Markdown export uses a ZIP instead of bare Markdown

In bare Markdown, images are relative paths, so distributing the `.md` alone leaves images invisible. After packaging into a ZIP:
- The `.md` and the `images/` directory are distributed together, so relative paths work naturally
- Users can extract and use it immediately, with no need to arrange images manually

### Why MarkdownExportHelper is designed as internal static

- The export logic is purely functional transformation (input string/stream → output string/stream), stateless, with no external dependencies (OSS is passed in as a parameter)
- No DI lifecycle management is needed, avoiding needless scoped/transient allocations
- The endpoint layer handles HTTP validation and orchestration; the helper layer holds pure, unit-testable logic

### OSS download failure tolerance

Export is a best-effort scenario: a single image failure should not block the export of the whole document. Therefore:
- `BuildMarkdownZipAsync` / `ReplaceImagePathsBase64Async` / `ReplaceImagePathsPresignedAsync` all use try/catch inside the loop
- Failures only log `LogWarning` (including `ImagePath`) and processing continues with the next image

## Error Handling

| Scenario | Handling |
|------|------|
| File not found | 404 + `DOCTHECA_FILE_NOT_FOUND` |
| Parse record not found | 404 + `DOCTHECA_PARSE_NOT_FOUND` |
| File not parsed | 422 + `DOCTHECA_FILE_NOT_PARSED` |
| Parse record not parsed | 422 + `DOCTHECA_PARSE_NOT_PARSED` |
| Single image OSS download failure | Log a Warning, skip that image |
| Single image presigning failure | Log a Warning, skip that image |

## External Dependencies

| Interface | Capability provided | Module |
|------|---------|---------|
| `IDocumentParseService` | Parse record / image list queries | `Doctheca.Domain.Services` |
| `IDocumentFileService` | File queries | `Doctheca.Domain.Services` |
| `IOssService` | File upload/download / presigned URLs | `Doctheca.Common.Oss` (vendored from ruoyu.common) |
| `Markdig` | Markdown → HTML conversion | NuGet package |

## DI Registration

```csharp
// Program.cs — endpoint registration (no authentication; internal admin backend)
app.MapDocumentExportEndpoints();
```

`MarkdownExportHelper` is `internal static` and needs no DI registration. `IDocumentParseService` / `IDocumentFileService` / `IOssService` are already registered in Program.cs, and the export endpoints use them directly via parameter injection.
