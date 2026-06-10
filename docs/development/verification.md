# Verification

How to verify `ruoyu.docretrieval` is running correctly.

## Health Check

```bash
curl http://localhost:5012/health
```

Expected response:

```json
{ "status": "healthy", "timestamp": "..." }
```

## Upload a Test Document

```bash
curl -X POST http://localhost:5012/admin/documents/upload \
  -F "file=@test.pdf" \
  -F "title=Test Document" \
  -F "subject=Mathematics" \
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

Supports query parameters: `page`, `pageSize`, `status`, `subject`, `grade`, `keyword`, `year`.

Example with filters:

```bash
curl "http://localhost:5012/admin/documents?subject=Mathematics&grade=G10&page=1&pageSize=10"
```

## Check Document Status

Replace `{id}` with the `document_id` returned from upload:

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
    "status": "completed",
    "jobs": [
      {
        "job_id": "...",
        "status": "completed",
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

Document statuses: `pending` → `processing` → `completed` (or `failed`).

## Run Tests

From the `backend/ruoyu.docretrieval/` directory:

```bash
dotnet test test/Ruoyu.Study.DocRetrieval.Tests
```

To filter by feature module:

```bash
dotnet test --filter "FullyQualifiedName~DocumentUpload"
dotnet test --filter "FullyQualifiedName~ExactSearch"
dotnet test --filter "FullyQualifiedName~DocumentDeletion"
```

## Search Test Endpoint

Requires OpenSearch (or database fallback) to be available:

```bash
curl "http://localhost:5012/admin/documents/search-test?query=algebra&pageSize=5"
```

Query parameters: `query` (required), `phrase` (boolean, default false), `pageSize` (1–100, default 20), `pageToken` (optional cursor).
