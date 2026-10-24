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

Updates `subject`/`grade`/`year` (null fields are not modified); best-effort OpenSearch sync (update_by_query), failures only log a Warning. The optional `structureDefinitionId` field chooses the document's structure definition (`""` clears it; 400 for an unknown id; 409 `DOCTHECA_DOCUMENT_STRUCTURE_IN_USE` while marked nodes exist) and is validated before the other fields are applied — see [DocumentStructure](./DocumentStructure.md).

### DELETE /admin/document-files/{id}

1. Collect **legacy** OSS paths (`file_path` + the `zip_path` and image paths of legacy parses; the image_path of new parses is a StructaDoc asset id and takes no part in OSS cleanup).
2. Delete local database records (cascading parses → blocks/images).
3. Best-effort cleanup of OSS objects (a single failure does not block; returns `ossDeleted`/`ossFailed`).
4. When `structadoc_document_id` is present: first best-effort cancel any Parse Run not in a terminal state, then `DELETE /api/v1/documents/{id}` (202 accepted counts as deleted; failures log a Warning and return `structaDocDeleted`).
5. Best-effort deletion of the OpenSearch index.

### POST /admin/document-files/{id}/parse

Validation sequence: modelVersion ∈ {`vlm`, `pipeline`} → file exists → has source (409 `DOCTHECA_DOCUMENT_SOURCE_REQUIRED`) → no in-progress parse with the same modelVersion (422 `DOCTHECA_PARSE_IN_PROGRESS`) → StructaDoc configured (503 `DOCTHECA_STRUCTADOC_NOT_CONFIGURED`). On success, a parse record with `status=pending` is created; for the actual submission and polling see [DocumentParse](./DocumentParse.md).

## Data

Key columns of `document_files`: `file_path` (nullable, legacy only), `structadoc_document_id` (nullable uuid; both absent for source-free books), `content_type`, `created_by`, `subject`/`grade`/`year`, `created_at`/`updated_at` (maintained by trigger). See [database/tables/document_files.md](../database/tables/document_files.md).

## Validation

- Unit tests: `DocumentFileServiceTests/` (including Attach reference backfill), `Authentication/DocumentFileUploadAuthorizationTests` (createdBy, 503 when not configured), `Authentication/AuthorizationBoundaryTests` (401/403 boundaries), `DocumentFileDeleteCleanupTests` (legacy path collection).
- Command: `dotnet test src/Doctheca.sln --configuration Release` (in this service's directory).

## Books authored before a source exists (#133)

`POST /admin/document-files` accepts JSON `{ "fileName": "PEP English Grade 9", "subject": "English", "grade": "9", "year": "2026", "structureDefinitionId": "<definition UUID>" }` and returns 201. `fileName` is the logical book title (trimmed, 1–500 characters); optional subject/grade/year have limits 50/20/10. A supplied definition must exist. Invalid fields return 400 with field reasons and no write. Creation records the authenticated UUID subject, or null for a non-UUID subject. It needs no configured StructaDoc, object store or search service and creates no parse, job, content, image, index entry or remote object.

Both source references are null and `contentType` is the empty string for a book without a file. Administrator list/detail add `hasSource` and `sourceState` (`structure-only` or `with-source`) derived from the committed source reference/path, independently of parse status. Detail includes subject/grade/year; list includes metadata and the selected definition. The optional list `sourceState` filter composes with name/latest-parse filters and pagination. A source-free book has null parse status and an empty parse list; an uploaded but unparsed book has `hasSource=true`.

`POST /admin/document-files/{id}/source` attaches the first multipart `file` to that same book. It uses the existing supported PDF/Word/PowerPoint MIME types and 200 MB limit. Missing book returns 404; an existing source returns 409 `DOCTHECA_DOCUMENT_SOURCE_EXISTS` before upload. Unconfigured upstream returns 503; provider errors use fixed, redacted messages. Upload is outside the document row lock. Compare-and-attach under the common owner lock lets only one concurrent upload win; it changes the upstream reference and validated MIME, retaining title, metadata, definition, tree, chapters and audio. It never enqueues parsing. Streams are disposed. A definitely unattached new source is cleaned best-effort after a fresh owner-serialized reference check; an unresolved database/reference outcome defers deletion.

Parsing without a source returns 409 `DOCTHECA_DOCUMENT_SOURCE_REQUIRED` before configuration checks. The shared enqueue transaction repeats this check before inserting a job. Attach a source, explicitly start VLM/Pipeline parsing, then explicitly bind existing chapters through the [document tree editor](DocumentStructure.md#document-current-tree-authoring-133). Successful reparse detaches content bindings while preserving chapters and audio; failures preserve committed state. Optional LLM analysis fills only fields still empty after taking the document lock, so a newer human value wins.

Metadata PUT also accepts `fileName`; null means unchanged and empty strings clear optional fields. A title cannot become blank. Title, metadata and definition are validated and committed together under the document lock. A definition change while any current or historical nodes use it returns the existing 409 and changes no metadata.

In the console: **New book → choose metadata/definition → Edit book chapters → add/reorder Unit/Section nodes → Save → attach first source → explicitly parse → select real parse in chapter editor → bind existing chapters → Save**. The `#document-structure/{documentFileId}` page loads no blocks until a real successful parse is selected. It supports unbound chapters and unsaved-change protection. Existing file-first upload, `#structure/{parseId}`, content/export and parse audio links retain their meanings. Document audio authoring and the complete listening workflow remain follow-up work under #128/#134.

All new writes inherit administrator authorization, hosted Cookie CSRF and security response headers. Names are rendered as text. No credentials, provider problem bodies or storage fields are added to the new responses/logs. There is no new configuration, package, schema migration or container change. Reverting the application to #132 removes these entry points but retains authored records; it cannot turn a source-free book into a parsed file. Older-schema recovery still requires the verified backup and matching image/configuration described in [migration recovery](../database/migrations.md#document-owned-structure-132).
