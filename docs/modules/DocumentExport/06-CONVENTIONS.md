# DocumentExport — Naming and Style Conventions

## Naming Conventions

| Type | Convention | Example |
|------|------|------|
| Endpoint static class | `XxxEndpoints` | `DocumentExportEndpoints` |
| Helper static class | `XxxHelper`, internal | `MarkdownExportHelper` |
| Image model | `DocumentParseImageModel` | — |
| Method | PascalCase, Async suffix | `ReplaceImagePathsBase64Async`, `BuildMarkdownZipAsync` |
| Parameter | camelCase | `markdownContent`, `ossService`, `parseId` |
| Local variable | camelCase | `htmlBody`, `zipFileName`, `presignedUrl` |

## Method Naming Conventions (MarkdownExportHelper)

- `ReplaceImagePathsRelative` — synchronous, no IO
- `ReplaceImagePathsBase64Async` — asynchronous, involves OSS downloads
- `ReplaceImagePathsPresignedAsync` — asynchronous, involves presigned URL generation
- `BuildMarkdownZipAsync` — asynchronous, involves OSS downloads
- `BuildHtmlStream` — synchronous, pure in-memory transformation

Asynchronous methods always carry the `Async` suffix; synchronous methods do not add a `Sync` suffix.

## Logging Conventions

- Image download failure: `logger.LogWarning(ex, "Failed to download image for export: {ImagePath}", img.ImagePath)`
- Base64 download failure: `logger.LogWarning(ex, "Failed to download image for HTML export: {ImagePath}", img.ImagePath)`
- Presigning failure: `logger.LogWarning(ex, "Failed to generate presigned URL for image: {ImagePath}", img.ImagePath)`

All per-image failures use `LogWarning` (recoverable, does not block the export), with the exception object and `ImagePath` for localization.

## Error Message Conventions

HTTP response messages are in Chinese (end-user facing); error codes are English constants:

| Error code | Message | Scenario |
|--------|------|------|
| `DOCTHECA_FILE_NOT_FOUND` | `File not found` | File-level endpoints |
| `DOCTHECA_FILE_NOT_PARSED` | `File is not parsed yet` | File-level endpoints |
| `DOCTHECA_PARSE_NOT_FOUND` | `Parse record not found` | Parse-level endpoints |
| `DOCTHECA_PARSE_NOT_PARSED` | `Parse record is not parsed yet` | Parse-level endpoints |

Uniform response format: `{ "success": false, "message": "...", "errorCode": "..." }`.

## Null Handling and Fault Tolerance Semantics

| Value / scenario | Semantics |
|-----------|------|
| `parse.MarkdownContent ?? ""` | Falls back to an empty string when the parse content is null; never throws on null |
| `file?.FileName ?? "document"` | For parse-level export, the file name falls back to `"document"` when the file has been deleted |
| Single image OSS download failure | Skip that image; do not block the whole export |
| Path not present in the markdown | `Replace` has no effect; the original text is kept |

## Path Handling Conventions

- Image references in Markdown are matched in three formats: `(<path>)`, `src="<path>"`, `src='<path>'`
- Relative paths uniformly point to `images/{ImageName}` (lowercase directory name + forward slash)
- Base64 data URI format: `data:{ContentType};base64,{base64}` (MimeType taken from `img.ContentType`, default `image/jpeg`)

## ZIP Packaging Conventions

- Markdown entry: `{fileName}.md`, `CompressionLevel.Optimal`
- Image entries: `images/{ImageName}`, `CompressionLevel.Fastest`
- `ZipArchive` uses `leaveOpen: true` to keep the MemoryStream usable after it is closed
- Reset `ms.Position = 0` before returning

## HTML Template Conventions

- Document type: `<!DOCTYPE html>`
- Language: `<html lang="zh-CN">`
- Encoding: `charset=UTF-8`
- Title: `WebUtility.HtmlEncode(fileName)` (XSS prevention)
- Styles: inline `<style>`, no external CSS
- Conversion: `Markdig.Markdown.ToHtml(markdownContent)`

## Dispose Conventions

- `using var imgStream = await ossService.DownloadAsync(...)` — each image stream is released individually
- `ZipArchive` is wrapped in `using`; `leaveOpen: true` only spares the underlying `MemoryStream`
- `StringBuilder` / `MemoryStream` need no explicit Dispose

## Endpoint Registration Conventions

- Grouped with `MapGroup`: `/admin/document-files`, `/admin/document-parses`
- Path parameter constraints: `{id:guid}`, `{parseId:guid}`
- Export endpoints require the `DocthecaAdmin` policy and Identity `role=admin`
