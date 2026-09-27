# document_files — Document File Table

> This file is the single source of truth for the `document_files` table.

## Design Background

The `document_files` table stores file metadata for the document parsing flow; parsing is driven by `StructaDocParseWorker` (ADR-0009). Originals of new documents are stored under StructaDoc's primary ownership, and this table only keeps the `structadoc_document_id` reference; `file_path` is used only by legacy (pre-migration uploaded) records.

`document_files` is intentionally kept lean: it stores only file metadata, and **all parse-related fields** (status, markdown, task_id, etc.) have been split into the `document_parses` table.

## Field Inventory

| Field | Type | Constraints | Default | Description |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | File unique identifier |
| `file_name` | `VARCHAR(500)` | NOT NULL | | File name |
| `file_path` | `VARCHAR(500)` | NULL | | OSS storage path; only for legacy records (original files uploaded before the migration); NULL for new records |
| `structadoc_document_id` | `UUID` | NULL | | StructaDoc Document ID (required for new uploads; legacy files are back-filled after lazy upload on the first parse trigger) |
| `content_type` | `VARCHAR(100)` | NOT NULL | | MIME type (e.g. `application/pdf`) |
| `created_by` | `UUID` | NULL | | Uploader user ID |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | | Creation time |
| `updated_at` | `TIMESTAMP WITH TIME ZONE` | NULL | | Last update time |
| `subject` | `VARCHAR(50)` | NULL | | Subject metadata (English/Chinese/Math/Physics/Chemistry/Biology/Other; stored values are the Chinese literals defined in `DocthecaConstants`). Can be set manually via `PUT /admin/document-files/{id}/metadata`, or auto-filled by an LLM for missing fields after parsing completes (best-effort). See the [DocumentMetadataAnalysis module](../../modules/DocumentMetadataAnalysis/01-FEATURE.md) |
| `grade` | `VARCHAR(20)` | NULL | | Grade metadata (K/G1-G12). Same source as `subject` |
| `year` | `VARCHAR(10)` | NULL | | Year metadata (4 digits, e.g. 2024). Same source as `subject` |

## Indexes

| Index | Columns | Description |
|--------|-----|------|
| `PK_document_files` | `id` | Primary key |
| `IX_document_files_file_name` | `file_name` | Query by file name |

## Foreign Keys

- Referenced by: this table is the target of `document_parses.document_file_id`

## Special Notes

- **OSS path convention (legacy only)**: `documents/{year}/{month}/{day}/{file-id}/source.{ext}`
  - The same directory also holds `mineru-output.zip`, `layout.pdf`, and `images/*`
  - When a file is deleted, the entire directory is cleaned up together
- At least one of `file_path` and `structadoc_document_id` must be non-null; on deletion, the OSS object and the StructaDoc document are each cleaned up best-effort
