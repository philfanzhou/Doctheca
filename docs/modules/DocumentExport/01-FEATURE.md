# 01-FEATURE — DocumentExport Document Export

## Feature Overview

Exports the Markdown content of parsed documents (`document_parses` with status `parsed`) as downloadable files. Supports two export dimensions (file-level / parse-level) and two export formats (Markdown / HTML), for a total of 4 endpoints. Markdown export packages the Markdown body and images into a ZIP; HTML export inlines images as Base64 and converts them via Markdig into a self-contained HTML document.

## Background

Images in parse output come from two sources (ADR-0009): for **legacy parses**, `markdown_content` references OSS absolute paths (e.g. `documents/mineru/task-123/abc.jpg`) and the bytes are read from OSS; for **new parses**, the Markdown references relative paths (`images/<name>`) and the bytes are streamed from StructaDoc Assets via `ParseImageContentSource`. Markdown distributed as-is cannot load images in offline environments. This feature:

1. Provides file-level export (takes the latest parse record by `document_file_id`)
2. Provides parse-level export (directly specifies a parse by `parse_id`)
3. Markdown export rewrites image paths to relative paths (`images/<name>`) and packages everything into a ZIP to guarantee offline usability
4. HTML export downloads images and inlines them as Base64, producing a single self-contained HTML file

## User Stories

- **As a teacher**: after I upload lecture notes and parsing completes, I can download a Markdown source package (with images) in one click for further editing
- **As a teacher**: I can download the HTML version directly and open it in a browser; images are inlined with no network access needed
- **As an operator**: I can export a specific parse record directly, without depending on "the latest parse"
- **As an operator**: during export, if an image source (OSS or StructaDoc) fails to download a particular image, that image is skipped without blocking the whole export

## Functional Requirements

### FR-01: File-level Markdown export (ZIP)
- `GET /admin/document-files/{id}/export/markdown`
- Takes the file's latest parse record, uniformly rewrites image references in `MarkdownContent` to relative paths `images/<ImageName>`, and packages them into `{fileName}_markdown.zip`
- The ZIP contains one `.md` file (Optimal compression) and one `images/` directory (Fastest compression)

### FR-02: File-level HTML export
- `GET /admin/document-files/{id}/export/html`
- Takes the file's latest parse record, downloads images and inlines them as Base64 into the Markdown, then converts to HTML via `Markdig.Markdown.ToHtml`, returning `{fileName}.html`

### FR-03: Parse-level Markdown export (ZIP)
- `GET /admin/document-parses/{parseId}/export/markdown`
- Takes the parse record directly by `parseId`; logic identical to FR-01

### FR-04: Parse-level HTML export
- `GET /admin/document-parses/{parseId}/export/html`
- Takes the parse record directly by `parseId`; logic identical to FR-02

### FR-05: Three image path handling modes
- **Relative paths** (`ReplaceImagePathsRelative`): used for Markdown/ZIP export; paths are rewritten to `images/<ImageName>`
- **Base64 inline** (`ReplaceImagePathsBase64Async`): used for HTML export; images are downloaded and converted to `data:{ContentType};base64,...`
- **Browser URL** (`ReplaceImagePathsAsync` + URL resolver): used by the detail page — legacy parses generate OSS presigned URLs valid for 1 hour; new parses generate proxy endpoint URLs `/admin/document-parses/{parseId}/images/{imageId}/content`

### FR-06: Export precondition validation
- File not found → 404 `DOCTHECA_FILE_NOT_FOUND`
- Parse record not found → 404 `DOCTHECA_PARSE_NOT_FOUND`
- Parse status is not `parsed` → 422 `DOCTHECA_FILE_NOT_PARSED` / `DOCTHECA_PARSE_NOT_PARSED`

### FR-07: Image download failure tolerance
- A single image download failure (OSS or StructaDoc) → log a Warning, skip that image, do not block the whole export
- Browser URL generation failure → log a Warning, skip that image

## Acceptance Criteria

| AC | Description |
|----|------|
| AC-01 | File-level Markdown export returns a ZIP containing `.md` and `images/` |
| AC-02 | File-level HTML export returns self-contained HTML with images inlined as Base64 |
| AC-03 | Parse-level export produces the same result as file-level export (same parseId) |
| AC-04 | Missing file returns 404 + `DOCTHECA_FILE_NOT_FOUND` |
| AC-05 | Missing parse record returns 404 + `DOCTHECA_PARSE_NOT_FOUND` |
| AC-06 | Incomplete parse returns 422 + the corresponding error code |
| AC-07 | A single image download failure (OSS or StructaDoc) does not block the export; a Warning is logged |
| AC-08 | Image paths in Markdown export are relative paths `images/<ImageName>` |
| AC-09 | Images in HTML export use the `data:{ContentType};base64,...` format |

## Non-functional Requirements

| NFR | Description |
|-----|------|
| NFR-01 | Export is streamed (`Results.Stream`), never touching disk |
| NFR-02 | ZIP/HTML are built in memory (`MemoryStream`) and released after the request ends |
| NFR-03 | Image download failures are best-effort and do not block the whole export |
| NFR-04 | HTTP only, no gRPC |
| NFR-05 | Export does not change parse status or trigger re-parsing |
| NFR-06 | Logs record the export file name, parseId, and image download failure reasons |

## Data Sources

- **Markdown content**: `document_parses.markdown_content`
- **Image list**: `document_parse_images` (queried by `parse_id`)
- **Image binaries**: OSS (`IOssService.DownloadAsync`)
- **File name validation**: `document_files.file_name` (falls back to `"document"` for parse-level export when the file no longer exists)

## Interface Inventory

| Component | Change |
|------|------|
| `DocumentExportEndpoints` | Add 4 endpoints + `MapDocumentExportEndpoints` |
| `MarkdownExportHelper` | Add `ReplaceImagePathsRelative` / `ReplaceImagePathsBase64Async` / `ReplaceImagePathsPresignedAsync` / `BuildMarkdownZipAsync` / `BuildHtmlStream` |
| `IDocumentParseService` | Reuse existing `GetByIdAsync` / `GetLatestByFileIdAsync` / `GetImagesByParseIdAsync` |
| `IDocumentFileService` | Reuse existing `GetByIdAsync` |
| `IOssService` | Reuse existing `DownloadAsync` / `GetPresignedUrlAsync` |
| `Program.cs` | Register `MapDocumentExportEndpoints` |
