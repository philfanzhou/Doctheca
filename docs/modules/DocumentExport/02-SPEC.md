# 02-SPEC — DocumentExport Detailed Specification

## 1. HTTP Endpoints

### 1.1 ExportMarkdown — File-level Markdown export (ZIP)

```
GET /admin/document-files/{id:guid}/export/markdown
```

**Request parameters** (path): `id` — primary key of the `document_files` table

**Processing flow**:
1. `fileService.GetByIdAsync(id)` → 404 if null
2. `parseService.GetLatestByFileIdAsync(id)` → 422 if null or `Status != parsed`
3. `parseService.GetImagesByParseIdAsync(parse.Id)` — fetch the image list
4. `MarkdownExportHelper.ReplaceImagePathsRelative(markdownContent, images)` — rewrite paths
5. `MarkdownExportHelper.BuildMarkdownZipAsync(fileName, markdown, images, ossService, logger)` — build the ZIP
6. `Results.Stream(zipStream, "application/zip", "{name}_markdown.zip")`

**Responses**:
- 200: `application/zip` stream, file name `{fileName}_markdown.zip`
- 404: `{ "success": false, "message": "File not found", "errorCode": "DOCTHECA_FILE_NOT_FOUND" }`
- 422: `{ "success": false, "message": "File is not parsed yet", "errorCode": "DOCTHECA_FILE_NOT_PARSED" }`

### 1.2 ExportHtml — File-level HTML export

```
GET /admin/document-files/{id:guid}/export/html
```

**Processing flow**:
1. Same as steps 1-2 in 1.1
2. `parseService.GetImagesByParseIdAsync(parse.Id)`
3. `await MarkdownExportHelper.ReplaceImagePathsBase64Async(markdownContent, images, ossService, logger)` — inline images as Base64
4. `MarkdownExportHelper.BuildHtmlStream(fileName, markdown)` — convert to HTML
5. `Results.Stream(htmlStream, "text/html", "{name}.html")`

**Responses**:
- 200: `text/html` stream, file name `{fileName}.html`
- 404 / 422: same as 1.1

### 1.3 ExportParseMarkdown — Parse-level Markdown export (ZIP)

```
GET /admin/document-parses/{parseId:guid}/export/markdown
```

**Request parameters** (path): `parseId` — primary key of the `document_parses` table

**Processing flow**:
1. `parseService.GetByIdAsync(parseId)` → 404 if null
2. 422 if `Status != parsed`
3. `fileService.GetByIdAsync(parse.DocumentFileId)` → `fileName = file?.FileName ?? "document"`
4. Same as steps 3-6 in 1.1

**Responses**:
- 200: `application/zip` stream
- 404: `{ "success": false, "message": "Parse record not found", "errorCode": "DOCTHECA_PARSE_NOT_FOUND" }`
- 422: `{ "success": false, "message": "Parse record is not parsed yet", "errorCode": "DOCTHECA_PARSE_NOT_PARSED" }`

### 1.4 ExportParseHtml — Parse-level HTML export

```
GET /admin/document-parses/{parseId:guid}/export/html
```

**Processing flow**: the parse-record validation of 1.3 + the HTML build flow of 1.2.

**Responses**: same as 1.2, with the error codes of 1.3.

## 2. Route Registration

```csharp
// DocumentExportEndpoints.cs
public static WebApplication MapDocumentExportEndpoints(this WebApplication app)
{
    var fileGroup = app.MapGroup("/admin/document-files");
    fileGroup.MapGet("/{id:guid}/export/markdown", ExportMarkdown);
    fileGroup.MapGet("/{id:guid}/export/html", ExportHtml);

    var parseGroup = app.MapGroup("/admin/document-parses");
    parseGroup.MapGet("/{parseId:guid}/export/markdown", ExportParseMarkdown);
    parseGroup.MapGet("/{parseId:guid}/export/html", ExportParseHtml);

    return app;
}
```

Registered in `Program.cs`: app.MapDocumentExportEndpoints();

## 3. Specification of the Three Image Path Modes

### 3.1 ReplaceImagePathsRelative (synchronous)

Rewrites OSS absolute paths to relative paths; used for Markdown/ZIP export.

```
Input: markdown image segments; replacement rules (per img):
  "({img.ImagePath})"            → "(images/{img.ImageName})"
  "src=\"{img.ImagePath}\""      → "src=\"images/{img.ImageName}\""
  "src='{img.ImagePath}'"        → "src='images/{img.ImageName}'"
```

- **No OSS calls**; pure string replacement
- **Paths that do not match are left unchanged**

### 3.2 ReplaceImagePathsBase64Async (asynchronous)

Downloads images and converts them to Base64 data URIs; used for HTML export.

```
For each img:
  1. ossService.DownloadAsync(img.ImagePath) → Stream
  2. CopyTo MemoryStream → byte[]
  3. Convert.ToBase64String → base64
  4. dataUri = $"data:{img.ContentType};base64,{base64}"
  5. Replace (6 formats):
     "({img.ImagePath})"                        → "({dataUri})"
     "src=\"{img.ImagePath}\""                  → "src=\"{dataUri}\""
     "src='{img.ImagePath}'"                    → "src='{dataUri}'"
     "(images/{img.ImageName})"                 → "({dataUri})"
     "src=\"images/{img.ImageName}\""           → "src=\"{dataUri}\""
     "src='images/{img.ImageName}'"             → "src='{dataUri}'"
  catch Exception → logger.LogWarning, skip
```

- The replacement formats cover OSS paths, relative paths, and single/double quotes, so already-rewritten paths can be processed again
- `img.ContentType` defaults to `"image/jpeg"` (the `DocumentParseImageModel` default)

### 3.3 ReplaceImagePathsPresignedAsync (asynchronous, not currently wired into any endpoint)

Generates presigned URLs for browser viewing.

```
For each img:
  1. ossService.GetPresignedUrlAsync(img.ImagePath, 3600) → presignedUrl (valid for 1 hour)
  2. Replace (4 formats):
     "({img.ImagePath})"                        → "({presignedUrl})"
     "(images/{img.ImageName})"                 → "({presignedUrl})"
     "src=\"{img.ImagePath}\""                  → "src=\"{presignedUrl}\""
     "src=\"images/{img.ImageName}\""           → "src=\"{presignedUrl}\""
  catch Exception → logger.LogWarning, skip
```

> **Note**: this method is **not actually wired into** the current endpoint call chain. It is kept as a helper capability and may be enabled in the future for "in-browser preview" scenarios. Wiring it in does not affect the behavior of the existing 4 endpoints.

## 4. ZIP Packaging Specification — BuildMarkdownZipAsync

```
Input: fileName, markdownContent, images, ossService, logger
Output: MemoryStream (Position = 0)

1. new MemoryStream()
2. new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)
3. Create entry "{Path.GetFileNameWithoutExtension(fileName)}.md" (CompressionLevel.Optimal)
   - Write markdownContent with StreamWriter
4. For each img:
   - ossService.DownloadAsync(img.ImagePath)
   - Create entry $"images/{img.ImageName}" (CompressionLevel.Fastest)
   - Write via CopyToAsync
   catch Exception → logger.LogWarning, skip
5. ms.Position = 0; return ms
```

- Text uses Optimal (higher compression ratio); images use Fastest (images are already compressed, so a high compression ratio is unnecessary)
- `leaveOpen: true` ensures the MemoryStream remains usable after the ZipArchive is closed

## 5. HTML Conversion Specification — BuildHtmlStream

```
Input: fileName, markdownContent
Output: MemoryStream

1. Markdig.Markdown.ToHtml(markdownContent) → htmlBody
2. Build the complete HTML document:
   - "<!DOCTYPE html>"
   - "<html lang=\"zh-CN\">"
   - <head>: charset UTF-8, viewport, title=HtmlEncode(fileName), inline <style>
   - <body>: htmlBody
3. Encoding.UTF8.GetBytes → MemoryStream
```

**Inline CSS** covers: `body` (font/width/line height/color), `img` (max-width), `table`/`th`/`td` (borders), `blockquote`, `code`, `pre`.

## 6. Implementation Steps

### 6.1 ExportMarkdownCore (shared)

```
1. parseService.GetImagesByParseIdAsync(parse.Id) → images
2. MarkdownExportHelper.ReplaceImagePathsRelative(parse.MarkdownContent ?? "", images) → markdownContent
3. MarkdownExportHelper.BuildMarkdownZipAsync(fileName, markdownContent, images, ossService, logger) → zipStream
4. zipFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_markdown.zip"
5. Results.Stream(zipStream, "application/zip", zipFileName)
```

### 6.2 ExportHtmlCore (shared)

```
1. parseService.GetImagesByParseIdAsync(parse.Id) → images
2. await MarkdownExportHelper.ReplaceImagePathsBase64Async(parse.MarkdownContent ?? "", images, ossService, logger) → markdownContent
3. MarkdownExportHelper.BuildHtmlStream(fileName, markdownContent) → htmlStream
4. htmlFileName = $"{Path.GetFileNameWithoutExtension(fileName)}.html"
5. Results.Stream(htmlStream, "text/html", htmlFileName)
```

### 6.3 Parse-level export file name fallback

For parse-level export, if the associated file has been deleted, `file?.FileName ?? "document"` — ensures the ZIP/HTML file name never falls back to empty.

## 7. Error Handling

| Scenario | HTTP | Error code | Handling |
|------|------|--------|------|
| File not found | 404 | `DOCTHECA_FILE_NOT_FOUND` | File-level endpoints |
| Parse record not found | 404 | `DOCTHECA_PARSE_NOT_FOUND` | Parse-level endpoints |
| File not parsed | 422 | `DOCTHECA_FILE_NOT_PARSED` | File-level endpoints |
| Parse record not parsed | 422 | `DOCTHECA_PARSE_NOT_PARSED` | Parse-level endpoints |
| Single image OSS download failure | — | — | Log a Warning, skip that image |
| Single image presigning failure | — | — | Log a Warning, skip that image |

## 8. Testing Strategy

### 8.1 Unit Tests (UT)

Pure logic tests with no dependency on HTTP / OSS / database. Located in `DocumentExportLogicTests.cs`, 12 tests in total.

| # | Test method | Coverage |
|---|---------|------|
| UT-DE-01 | `MarkdownPathReplacement_ReplacesS3PathWithRelativePath` | FR-05 (relative path) |
| UT-DE-02 | `MarkdownPathReplacement_ReplacesSrcAttribute` | FR-05 (double-quoted src) |
| UT-DE-03 | `MarkdownPathReplacement_ReplacesSrcAttribute_SingleQuotes` | FR-05 (single-quoted src) |
| UT-DE-04 | `MarkdownPathReplacement_ReplacesMixedQuoteFormats` | FR-05 (mixed formats) |
| UT-DE-05 | `MarkdownPathReplacement_HandlesMultipleImages` | FR-05 (multiple images) |
| UT-DE-06 | `MarkdownPathReplacement_DoesNotReplaceIfPathNotFound` | FR-05 (path not matched) |
| UT-DE-07 | `HtmlExport_ReplacesS3PathWithDataUri` | FR-05 (Base64) |
| UT-DE-08 | `Markdig_ConvertsMarkdownToHtml` | FR-02 (Markdig basic conversion) |
| UT-DE-09 | `Markdig_ConvertsMarkdownWithImageToHtml` | FR-02 (Markdig image conversion) |
| UT-DE-10 | `HtmlTemplate_GeneratesValidHtmlDocument` | FR-02 (HTML template) |
| UT-DE-11 | `ZipExport_ContainsMarkdownAndImages` | FR-01 (ZIP structure) |
| UT-DE-12 | `Base64Conversion_RoundTripsCorrectly` | FR-05 (Base64 round trip) |

### 8.2 Integration Tests (not implemented)

| # | Test case | Coverage |
|---|---------|------|
| IT-DE-01 | File-level Markdown export returns a valid ZIP | AC-01 |
| IT-DE-02 | File-level HTML export returns HTML containing Base64 | AC-02 |
| IT-DE-03 | Parse-level export matches file-level export | AC-03 |
| IT-DE-04 | Missing file returns 404 | AC-04 |
| IT-DE-05 | Missing parse record returns 404 | AC-05 |
| IT-DE-06 | Not parsed returns 422 | AC-06 |

## 9. Impact Scope

| Component | Impact |
|------|------|
| `DocumentExportEndpoints` | Add 4 endpoints + `MapDocumentExportEndpoints` |
| `MarkdownExportHelper` | Add 5 static methods |
| `Program.cs` | Register `MapDocumentExportEndpoints` |
| `document_parses` table | Read-only, not modified |
| `document_parse_images` table | Read-only, not modified |
| `document_files` table | Read-only, not modified |
| OSS | Read-only downloads, no writes |

## 10. Deployment Scripts

**Not modified**. The export feature reuses the existing OSS / database / parse pipeline configuration; no new environment variables or configuration sections are required.
