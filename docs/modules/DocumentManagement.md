# DocumentManagement — Document File Management

> Single capability document consolidated from the original DocumentManagement six-part set. Storage ownership migrates per ADR-0009, Doctheca–StructaDoc parse migration: StructaDoc owns new document originals; locally only references and metadata are stored.

## Capability Overview

Full lifecycle management of document files: upload (forwarded to StructaDoc), paginated listing, detail view, metadata update, deletion (with coordinated cleanup of local data, legacy OSS objects, and the StructaDoc document), and parse triggering. The local `document_files` table stores the file name, MIME, subject/grade/year, and the `structadoc_document_id` reference; legacy records keep `file_path` (the OSS path).

## Endpoints and Contracts

### POST /admin/document-files/upload

- MIME whitelist: PDF, DOC, DOCX, PPT, PPTX; size limit 200MB (validated on this service's side).
- File bytes are forwarded directly to StructaDoc `POST /api/v1/documents` (no longer written to OSS); the local record stores `structadoc_document_id`, and `content_type` uses StructaDoc's content sniffing result.
- `created_by` is taken from the admin JWT `sub`; when missing or not a UUID it stays null without affecting the upload.
- Error codes:

| Scenario | Status code | errorCode |
|------|--------|-----------|
| StructaDoc not configured | 503 | `DOCTHECA_STRUCTADOC_NOT_CONFIGURED` |
| Empty file | 400 | `DOCTHECA_FILE_REQUIRED` |
| Unsupported format (local whitelist or StructaDoc 415) | 400 | `DOCTHECA_FILE_FORMAT_UNSUPPORTED` |
| Exceeds the local 200MB / StructaDoc limit (413) | 400 | — |
| StructaDoc rejects the API key (401/403) | 502 | `DOCTHECA_STRUCTADOC_UNAUTHORIZED` |
| Other StructaDoc failures | 502 | `DOCTHECA_STRUCTADOC_ERROR` |

### GET /admin/document-files

Pagination via `page`/`pageSize`; `fileName` fuzzy search; `parseStatus` filter (`unparsed` or `pending`/`parsing`/`parsed`/`failed`, filtered in memory by the latest parse); returns total/page/pageSize/totalPages.

### GET /admin/document-files/{id}

- Returns file info + all parses (status, markdown, the contentList family, errorMessage, parsedAt, images).
- Dual-path image URLs: **new parses** (with `structadoc_parse_run_id`) → proxy endpoint `/admin/document-parses/{parseId}/images/{imageId}/content`; **legacy parses** → OSS presigned URL (3600s, falling back to the original path on failure).
- Image references inside the markdown are replaced according to the same source.

### PUT /admin/document-files/{id}/metadata

Updates `subject`/`grade`/`year` (null fields are not modified); best-effort OpenSearch sync (update_by_query), failures only log a Warning.

### DELETE /admin/document-files/{id}

1. Collect **legacy** OSS paths (`file_path` + the `zip_path` and image paths of legacy parses; the image_path of new parses is a StructaDoc asset id and takes no part in OSS cleanup).
2. Delete local database records (cascading parses → blocks/images).
3. Best-effort cleanup of OSS objects (a single failure does not block; returns `ossDeleted`/`ossFailed`).
4. When `structadoc_document_id` is present: first best-effort cancel any Parse Run not in a terminal state, then `DELETE /api/v1/documents/{id}` (202 accepted counts as deleted; failures log a Warning and return `structaDocDeleted`).
5. Best-effort deletion of the OpenSearch index.

### POST /admin/document-files/{id}/parse

Validation sequence: modelVersion ∈ {`vlm`, `pipeline`} → file exists → no in-progress parse with the same modelVersion (422 `DOCTHECA_PARSE_IN_PROGRESS`) → StructaDoc configured (503 `DOCTHECA_STRUCTADOC_NOT_CONFIGURED`). On success, a parse record with `status=pending` is created; for the actual submission and polling see [DocumentParse](./DocumentParse.md).

## Data

Key columns of `document_files`: `file_path` (nullable, legacy only), `structadoc_document_id` (nullable uuid; one of the two is required for new documents), `content_type`, `created_by`, `subject`/`grade`/`year`, `created_at`/`updated_at` (maintained by trigger). See [database/tables/document_files.md](../database/tables/document_files.md).

## Validation

- Unit tests: `DocumentFileServiceTests/` (including Attach reference backfill), `Authentication/DocumentFileUploadAuthorizationTests` (createdBy, 503 when not configured), `Authentication/AuthorizationBoundaryTests` (401/403 boundaries), `DocumentFileDeleteCleanupTests` (legacy path collection).
- Command: `dotnet test src/Doctheca.sln --configuration Release` (in this service's directory).
