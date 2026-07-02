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

## Upload Document

```bash
curl -X POST http://localhost:5012/admin/documents/upload \
  -F "file=@test.pdf" \
  -F "title=Test Document" \
  -F "subject=English" \
  -F "grade=G10" \
  -F "year=2024"
```

Required form fields: `file`, `title`, `subject`, `grade`, `year`. Optional: `tags`.

Supported file types: PDF, Word (.doc/.docx), PowerPoint (.ppt/.pptx). Max size: 200 MB.

Expected response:

```json
{
  "success": true,
  "data": {
    "document_id": "...",
    "title": "Test Document",
    "job_id": "...",
    "status": "pending"
  }
}
```

## List Documents

```bash
curl http://localhost:5012/admin/documents
```

Query parameters: `page`, `pageSize`, `status`, `subject`, `grade`, `keyword`, `year`.

With filters:

```bash
curl "http://localhost:5012/admin/documents?subject=English&grade=G10&page=1&pageSize=10"
```

## Get Document Detail

Replace `{id}` with the `document_id` from upload response:

```bash
curl http://localhost:5012/admin/documents/{id}
```

Expected response:

```json
{
  "success": true,
  "data": {
    "id": "...",
    "title": "Test Document",
    "source_type": "pdf",
    "file_hash": "...",
    "file_size": 12345,
    "language": "en",
    "subject": "English",
    "grade": "G10",
    "year": "2024",
    "tags": null,
    "status": "ready",
    "created_at": "...",
    "updated_at": "..."
  }
}
```

## Check Document Status

Replace `{id}` with the `document_id` from upload response:

```bash
curl http://localhost:5012/admin/documents/{id}/status
```

Expected response:

```json
{
  "success": true,
  "data": {
    "document_id": "...",
    "title": "Test Document",
    "status": "ready",
    "jobs": [
      {
        "job_id": "...",
        "status": "success",
        "parser_version": "...",
        "ocr_version": "...",
        "error_message": null,
        "started_at": "...",
        "finished_at": "..."
      }
    ]
  }
}
```

Document status flow: `pending` -> `processing` -> `ready` (or `failed`).

Job status flow: `pending` -> `processing` -> `success` (or `failed`).

## Update Document Metadata

Only documents in `ready` status can be updated:

```bash
curl -X PUT http://localhost:5012/admin/documents/Test%20Document/metadata \
  -H "Content-Type: application/json" \
  -d '{"subject":"English","grade":"G11","year":"2025"}'
```

## Delete Document

By ID (recommended):

```bash
curl -X DELETE http://localhost:5012/admin/documents/{id}
```

By title (alternative):

```bash
curl -X DELETE http://localhost:5012/admin/documents/by-title/Test%20Document
```

## Search Test Endpoint

Requires OpenSearch (or database fallback) to be available:

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
dotnet test --filter "FullyQualifiedName~DocumentDeletion"
```
