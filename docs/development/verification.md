# Verification Guide

How to verify `ruoyu.doclibrary` is running correctly.

## Health Check

```bash
curl http://localhost:5012/health
```

Expected response:

```json
{ "status": "healthy", "timestamp": "..." }
```

## Upload Document File

```bash
curl -X POST http://localhost:5012/admin/document-files/upload \
  -F "file=@test.pdf"
```

Required form field: `file`. Optional: `subject`, `grade`, `year`（可由 LLM 在解析完成后自动填充）。

Supported file types: PDF, Word (.doc/.docx), PowerPoint (.ppt/.pptx). Max size: 200 MB.

Expected response:

```json
{
  "success": true,
  "data": {
    "fileId": "...",
    "fileName": "test.pdf",
    "status": "uploaded"
  }
}
```

## List Document Files

```bash
curl http://localhost:5012/admin/document-files
```

Query parameters: `page`, `pageSize`, `status`, `subject`, `grade`, `year`, `search`.

With filters:

```bash
curl "http://localhost:5012/admin/document-files?subject=English&grade=G10&page=1&pageSize=10"
```

## Get Document File Detail

Replace `{id}` with the `fileId` from upload response:

```bash
curl http://localhost:5012/admin/document-files/{id}
```

## Trigger MinerU Parse

```bash
curl -X POST http://localhost:5012/admin/document-files/{id}/parse
```

## Check Parse Status

Replace `{parseId}` with the parse ID returned from parse trigger:

```bash
curl http://localhost:5012/admin/document-parses/{parseId}
```

Parse status flow: `pending_parse` -> `parsing` -> `parsed` (or `failed`).

## Update Document File Metadata

```bash
curl -X PUT http://localhost:5012/admin/document-files/{id}/metadata \
  -H "Content-Type: application/json" \
  -d '{"subject":"English","grade":"G11","year":"2025"}'
```

## Delete Document File

```bash
curl -X DELETE http://localhost:5012/admin/document-files/{id}
```

## Search Test Endpoint

Requires OpenSearch to be available:

```bash
curl "http://localhost:5012/admin/documents/search?query=algebra&pageSize=5"
```

With filters:

```bash
curl "http://localhost:5012/admin/documents/search?query=algebra&subject=English&grade=G10&pageSize=5"
```

Query parameters: `query` (required), `phrase` (boolean, default false), `pageSize` (1-100, default 20), `pageToken` (optional cursor), `subject`, `grade`, `year`, `documentTitle` (optional filters).

## Run Unit Tests

From `src/services/ruoyu.doclibrary/` directory:

```bash
dotnet test test/Ruoyu.Study.DocLibrary.Tests
```

Filter by module:

```bash
dotnet test --filter "FullyQualifiedName~DocumentUpload"
dotnet test --filter "FullyQualifiedName~ExactSearch"
```
