# DocumentParse — Document Parsing (StructaDoc Pipeline)

> Per ADR-0009, Doctheca–StructaDoc parse migration, the parsing pipeline has migrated from the self-maintained MinerU implementation to the external StructaDoc service. This document is the single capability document consolidated from the original DocumentParse six-part set.

## Capability Overview

- Asynchronous parse lifecycle: trigger → `pending` → Worker submits a StructaDoc Parse Run → `parsing` → terminal-state sync → `parsed` / `failed`.
- Originals and parse artifacts (Markdown, images, ZIP, normalized PDF) are stored by StructaDoc as the owner; this service only keeps documentId/parseRunId references and syncs Blocks/Markdown/Assets into local tables for search, detail, and export consumption.
- Legacy compatibility: parse records from before the migration are kept read-only (their `content_list*`/`model_json`/`layout_json`/`zip_path` columns no longer receive new data); files uploaded before the migration are lazily uploaded to StructaDoc by the Worker on the first parse trigger, with references backfilled.

## Endpoints and Contracts

| Method | Path | Description |
|------|------|------|
| POST | `/admin/document-files/{id}/parse?modelVersion=vlm\|pipeline` | Trigger a parse (for validation and error codes see [DocumentManagement](./DocumentManagement.md)) |
| GET | `/admin/document-parses/{parseId}/images/{imageId}/content` | New-parse image proxy: streams Asset bytes from StructaDoc; browsers authenticate via the admin Cookie, so it can be used directly in `<img>` |

Parse status enum: `pending` / `parsing` / `parsed` / `failed` (`document_parses.status`).
Triggering a parse when StructaDoc is not configured returns 503 + `DOCTHECA_STRUCTADOC_NOT_CONFIGURED`.

## Background Worker (StructaDocParseWorker)

Polls `pending` + `parsing` records every 5 seconds:

1. **pending**:
   - File has no `structadoc_document_id` but has `file_path` (legacy) → download from OSS and upload to StructaDoc, backfilling the reference (lazy migration); neither present → failed.
   - `POST parse-runs` (`Idempotency-Key` = parseId in "N" format; `modelVersion` carries `providerConfigId` when it hits `StructaDoc:ProviderConfigIdByModel`, otherwise the StructaDoc default Provider is used).
   - Success → `parsing`; `external_task_id` and `structadoc_parse_run_id` record the run id.
2. **parsing**: polls `GET /api/v1/parse-runs/{id}`:
   - `succeeded` → run result sync, then best-effort OpenSearch indexing and LLM metadata analysis;
   - `failed` / `cancelled` → local `failed`, with `error_message` carrying StructaDoc's `errorCode: errorMessage`;
   - run 404 → `failed` (the run no longer exists);
   - record has no `structadoc_parse_run_id` (leftover `parsing` from an interrupted migration) → `failed`, prompting a re-trigger.
3. **Failure semantics**: transient errors (network, timeout, 408/429/5xx) keep the current state and retry in the next round; permanent errors are written as `failed`.

## Result Sync (StructaDocParseResultSync)

- **Assets → `document_parse_images`**: `image_name` = asset.name; `image_path` = asset id (Guid string, **not an OSS path**); `content_type` = asset.mediaType (defaults to `image/jpeg`).
- **Blocks → `document_parse_blocks`** (ordered by `sequence`):
  - `page_id` = pageNumber − 1 (1-based → local 0-based; null → 0); `sort_index` increments within a page;
  - `block_type` = type (truncated to 20); `text_content` = content; `sub_type` = subtype;
  - `text_level`: valid positive subtype `heading-N` → N; otherwise exact lowercase type `title` → −2, every other new block → −1; `text_format` = contentFormat;
  - bbox: StructaDoc 0–1 normalized coordinates ×1000, aligned with the local 0–1000 convention; `score` = confidence;
  - `image_id`: block.assetId → local image record mapping; `block_data` = the block's normalized JSON.
  - `caption` / `footnote`: optional plain-text annotations retained exactly in typed columns and
    normalized `block_data`, including Unicode, line endings, empty/whitespace and null values.
    No trimming, truncation or HTML interpretation is performed; missing older fields remain null.
- **Heading classification (#106)** reads the full subtype before its stored 50-character truncation.
  The `heading-` prefix is ordinal case-insensitive; the suffix uses invariant `Int32.TryParse`
  with `NumberStyles.Integer` and must be positive. Leading zeros, optional plus and surrounding
  ASCII integer whitespace remain accepted. Zero, negatives, malformed and overflow suffixes do
  not supply a level. A positive subtype remains compatible on a non-title type. Canonical type
  matching is exact lowercase `title`; legacy type spellings are not reinterpreted. Historical
  positive levels and non-title rows (including MinerU body `0`) are preserved by the targeted
  data-only migration. Existing index copies require explicit reindex; no remote reparse occurs.
  See [classification migration and rollback](../database/migrations.md#level-less-title-classification-issue-106).
- **Markdown → `document_parses.markdown_content`**; status is set to `parsed` and `parsed_at` is recorded.
- **Optional Pages → [`document_parse_pages`](../database/tables/document_parse_pages.md)**:
  the bare array `{number,label,labelSource}` uses positive one-based physical numbers. Labels
  and their sources are preserved exactly, including null, empty, repeated and Unicode text.
  The complete set is validated before a transaction replaces all pages for this local parse.
  Missing/empty/malformed/duplicate pages or HTTP/network/JSON/timeout failure produce one fixed,
  content-free Warning and an empty replacement, so stale labels never count as a fresh sync.
  This fallback still allows Blocks/Markdown to reach `parsed`. A local replacement failure
  rolls back and uses the existing transient worker retry boundary; it cannot publish `parsed`.
  Caller cancellation propagates (including wrapped cancellation) rather than becoming a fallback.
  Cancellation after a page commit retains that truthful committed set without publishing success.
- **Physical page provenance**: normalized `block_data.pageNumber` must be a positive integer,
  agree with the stored `page_id + 1`, and belong to a StructaDoc-backed parse. Null upstream
  pages retain the existing `page_id=0` fallback but do not qualify for page 1's printed label.
  The typed batch read uses explicit local parse/page keys; labels are never identities.
- **Idempotency**: old images of this parse are deleted before syncing (blocks are deleted-then-inserted by the repository), so re-running after a crash is safe.

Page replacement is atomic on its own; the pre-existing image/Block/Markdown writes are not one
transaction with it. A later failure can retain committed pages until retry, never a partial page
set or a falsely published successful sync. The existing empty-Block behavior is unchanged.
The two admin [ordered-block views](DocumentStructure.md#ordered-blocks) expose optional nullable
`pageLabel` from these stored pages (#103), with one trusted parse/page batch per response,
null labels on optional lookup failure and propagated request cancellation. No remote fetch or
read-side mutation is performed. Search responses (#104), physical numbering, authentication and
the console remain unchanged. Old parses remain readable without labels and are not
re-synchronized automatically. See the
[upgrade/down-script procedure](../database/migrations.md#printed-page-label-upgrade-issue-102).

**Annotation search (#105).** A Block is indexed when any body/caption/footnote is non-whitespace,
including captioned images with null body. Each Block remains one document/hit. Stemmed queries
search all three fields; exact phrases stay inside one field. Snippets prefer the first matched
highlight in body, caption, footnote order, then the first non-whitespace raw field in that order.
Offsets remain the unspecified `0` sentinel. Index errors do not undo parsed persistence;
cancelled client reads cannot publish a successful partial sync. Annotations/provider errors are
not logged. No remote annotation backfill/re-sync or new UI is introduced. Deployments require the
[nullable-column migration and mapping4 reindex procedure](../database/migrations.md#block-annotation-upgrade-issue-105).

## StructaDoc Client Contract Summary (IStructaDocClient)

- Authentication: `Authorization: ApiKey <credential>`, requiring the `documents:write`, `parses:read`, `parses:write` scopes.
- `POST /api/v1/documents` (multipart field `file`, 201); `POST /api/v1/documents/{id}/parse-runs` (201 first time / 200 + `Idempotency-Replayed` on replay).
- `GET /api/v1/parse-runs/{id}`: terminal states are only `succeeded` / `failed` / `cancelled`.
- `GET .../blocks?limit=1000&afterSequence=`: must follow `nextSequence` paging until null.
- `GET .../pages`: a bare array, not the paginated Blocks envelope; optional labels require no
  additional scope or configuration. Error bodies and JSON details are not consumed into new
  diagnostics. The client preserves caller cancellation and disposes every response.
- `GET .../assets`, `.../markdown`, `.../assets/{assetId}/content` (direct byte stream, no signed URL).
- `DELETE /api/v1/documents/{id}` (202 accepted / 404 idempotent; cancel first when a non-terminal run exists); `DELETE /api/v1/parse-runs/{id}` (terminal states only).
- Errors: RFC 7807 problem+json (empty body for 401/403, must not be parsed); 408/429/5xx/network errors are classified as transient.

## Data and Configuration

- New columns: `document_parses.structadoc_parse_run_id`, `document_files.structadoc_document_id`; `document_files.file_path` becomes nullable (legacy use only). See [database/](../database/README.md).
- Configuration section `StructaDoc`: `BaseUrl` (Consul KV `service-endpoints.json`), `ApiKey` (injected via the start.sh environment variable `StructaDoc__ApiKey`, never committed to the repo), `TimeoutSeconds` (default 300), `ProviderConfigIdByModel` (optional, `vlm`/`pipeline` → StructaDoc Provider Config ID).
- Provider-side requirements: the MinerU Cloud Provider must configure `model_version=vlm`, `is_ocr`, `enable_formula`, `enable_table` to match pre-migration parse quality.

## Validation

- Unit/integration tests: `src/Tests/Doctheca.Tests/` (`StructaDoc/StructaDocClientTests`, `StructaDoc/StructaDocPagesClientTests`, `Parsing/StructaDocParseResultSyncTests`, `Parsing/StructaDocAnnotationSyncTests`, `Parsing/StructaDocPagesSyncTests`, `Parsing/DocumentParsePageIdentityTests`, `Database/DocumentParsePageRepositoryTests`, `Database/PageMigrationTests`, `Database/AnnotationMigrationTests`, `OpenSearch/OpenSearchAnnotationTests`, `Parsing/ParseImageContentSourceTests`, `StructaDocParseWorkerTests`).
- Command: `dotnet test src/Doctheca.sln --configuration Release` (in this service's directory).
- Before switching traffic, the `vlm` + batch endpoint combination must be validated with real documents in a real environment (ADR-0009 consequences clause).
