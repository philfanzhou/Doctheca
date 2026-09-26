# document_parses — Document Parse Record Table

> This file is the single source of truth for the `document_parses` table.

## Field Inventory

| Field | Type | Constraints | Default | Description |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | Parse record unique identifier |
| `document_file_id` | `UUID` | NOT NULL, FK → `document_files(id)` ON DELETE CASCADE | | Associated file |
| `model_version` | `VARCHAR(20)` | NOT NULL | `'vlm'` | Parse model version: `vlm` / `pipeline` |
| `status` | `VARCHAR(30)` | NOT NULL | `'pending'` | Parse status: `pending` / `parsing` / `parsed` / `failed` |
| `external_task_id` | `VARCHAR(100)` | NULL | | External parse task ID: MinerU task_id for legacy records, StructaDoc Parse Run ID for new records |
| `structadoc_parse_run_id` | `UUID` | NULL | | StructaDoc Parse Run ID; non-null means a new-pipeline record (determines the read path for images/artifacts) |
| `markdown_content` | `TEXT` | NULL | | Parse artifact Markdown (legacy: MinerU `full.md`; new: StructaDoc canonical Markdown Artifact) |
| `content_list` | `JSONB` | NULL | | Full `content_list.json` (structured block array v1); **legacy read-only column**, not written by the new pipeline |
| `content_list_v2` | `JSONB` | NULL | | Structured block array v2; **legacy read-only column**, not written by the new pipeline |
| `model_json` | `JSONB` | NULL | | Model inference results (incl. bbox coordinates, layout classification); **legacy read-only column** |
| `layout_json` | `JSONB` | NULL | | Layout analysis data (incl. per-page bbox coordinates); **legacy read-only column** |
| `zip_path` | `VARCHAR(500)` | NULL | | Path of the full MinerU ZIP in OSS; **legacy read-only column** (new parse artifacts are stored under StructaDoc's primary ownership) |
| `error_message` | `TEXT` | NULL | | Error message when parsing fails |
| `parsed_at` | `TIMESTAMP WITH TIME ZONE` | NULL | | Parse completion time |

## Indexes

| Index | Columns | Description |
|--------|-----|------|
| `PK_document_parses` | `id` | Primary key |
| `IX_document_parses_document_file_id` | `document_file_id` | Query latest parse by file |
| `IX_document_parses_status` | `status` | Filter by status (used for pending polling) |
| `IX_document_parses_model_version` | `model_version` | Query by model version |

## Storage Strategy

- **`markdown_content` (text)**: for direct display; a single document can reach 5MB+
- **`content_list_v2` (jsonb)**: legacy structured block array v2 (incl. bbox / classification, etc.), coexisting with v1; not written by the new pipeline.
- **`model_json` / `layout_json` (jsonb)**: legacy model inference and layout analysis data; not written by the new pipeline.
- **`zip_path` (varchar)**: path of the legacy full ZIP in OSS, as a **fallback**; raw artifacts of new parses (provider-archive and other Artifacts) are stored in StructaDoc and read via its API.
- **New-pipeline data plane**: local synchronized copies of blocks/images are written to `document_parse_blocks` / `document_parse_images` as usual, Markdown is written to `markdown_content`; see the [DocumentParse capability doc](../../modules/DocumentParse.md).

## Special Notes

- **Multiple model versions coexist**: the same `document_file_id` can simultaneously have both `vlm` and `pipeline` parse records without affecting each other. Re-parsing creates a new record; the old record is kept.
- Deleting a `document_file` automatically deletes all associated `document_parses` records via CASCADE, and `document_parse_blocks` and `document_parse_images` are also deleted via CASCADE
