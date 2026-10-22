# document_parse_blocks — Document Parse Block Table

> This file is the single source of truth for the `document_parse_blocks` table.

## Design Purpose

This table stores the **structured block rows** of parse artifacts. Legacy data was written by parsing historical MinerU `content_list.json`; new data is written by mapping StructaDoc Blocks synchronization (ADR-0009, `StructaDocParseResultSync`). It supports:
- Query by page (`WHERE parse_id=? AND page_id=?`)
- Query by type (`WHERE block_type='equation'`)
- Full-text search (`text_content` GIN index)
- Fetch in page order (`ORDER BY page_id, sort_index`)
- Fetch in global reading order (`ORDER BY sequence`, issue #89), which structure node ranges refer to

## Field Inventory

| Field | Type | Constraints | Default | Description |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | Block unique identifier |
| `parse_id` | `UUID` | NOT NULL, FK → `document_parses(id)` ON DELETE CASCADE | | Associated parse |
| `page_id` | `INT` | NOT NULL | | Owning page (0-indexed) |
| `sort_index` | `INT` | NOT NULL | | Reading order within the page (a per-page counter) |
| `sequence` | `INT` | NULL, UNIQUE with `parse_id` | | Global 0-based reading order within the parse (StructaDoc `sequence`). Nullable so the previous image keeps working; every row is numbered by the migration and by every sync |
| `block_type` | `VARCHAR(20)` | NOT NULL | | Type (common legacy values: `text` / `image` / `equation` / `code` / `table` / `list`; new records use StructaDoc canonical types: `title` / `text` / `list` / `table` / `formula` / `image` / `code` / `header` / `footer` / `footnote` / `unknown`; actual values are determined by the parse artifacts, and consumers must tolerate new values) |
| `text_content` | `TEXT` | NULL | | Text/HTML/LaTeX (block body) |
| `image_id` | `UUID` | NULL, FK → `document_parse_images(id)` ON DELETE SET NULL | | Only set when `block_type='image'` |
| `block_data` | `JSONB` | NOT NULL | | ⭐ Whole-block original JSON (fallback): legacy records hold the original MinerU content_list item; new records hold the normalized JSON of the StructaDoc block (camelCase) |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | `NOW()` | Creation time |
| `sub_type` | `VARCHAR(50)` | NULL | | [Gen 2] minerU secondary classification (e.g. `image_body`/`table_caption`/`text`/`ref_text`) |
| `text_level` | `INT` | NOT NULL | `-1` | [Gen 2] Heading level: 0=body,1=h1,2=h2...; non-heading text is `-1` |
| `text_format` | `VARCHAR(20)` | NOT NULL | `''` | [Gen 2] Text format (VLM backend): `latex`/`markdown`/`none` |
| `bbox_x0` | `REAL` | NULL | | [Gen 2] bbox top-left X (normalized to the 0-1000 pipeline convention) |
| `bbox_y0` | `REAL` | NULL | | [Gen 2] bbox top-left Y |
| `bbox_x1` | `REAL` | NULL | | [Gen 2] bbox bottom-right X |
| `bbox_y1` | `REAL` | NULL | | [Gen 2] bbox bottom-right Y |
| `score` | `REAL` | NULL | | [Gen 2] Confidence: legacy records hold the minerU VLM score; new records hold StructaDoc `confidence` (0-1) |
| `caption` | `TEXT` | NULL | | Exact optional StructaDoc plain-text caption; legacy rows may contain derived captions |
| `footnote` | `TEXT` | NULL | | Exact optional StructaDoc plain-text footnote (#105); NULL for older payloads and migrated rows |

## Indexes

| Index | Columns | Description |
|--------|-----|------|
| `PK_document_parse_blocks` | `id` | Primary key |
| `IX_document_parse_blocks_parse_id_page_id_sort_index` | `(parse_id, page_id, sort_index)` | Read in page order |
| `IX_document_parse_blocks_block_type` | `block_type` | Filter by type |
| `IX_document_parse_blocks_image_id` | `image_id` | Query by image association |
| `IX_document_parse_blocks_parse_id_sequence` | `(parse_id, sequence)` UNIQUE | Global reading order; NULLs never collide |

## Key Design Points

- **`block_data` stores the whole-block original JSON**: when the parse artifact format evolves and adds fields, **no schema change is needed**; all new fields live in the JSONB
- Frequently queried fields (page, type, text) are extracted into their own indexed columns — query performance is fine
- Rarely queried fields (angle, formula_latex) stay inside `block_data` — they do not consume structured storage space
- `image_id` is a soft association to `document_parse_images` (SET NULL) — when an image is deleted the block is kept but loses its image reference
- **Structured dimension fields**: `sub_type`/`text_level`/`text_format`/`bbox_x0..y1`/`score`/`caption`/`footnote` are independent columns; `block_data` still keeps the normalized block, including annotations.
- **Annotations are untrusted plain text**: caption/footnote preserve null, empty/whitespace, Unicode and line endings exactly, without trimming, truncation, HTML interpretation or logging. An image/table with null body can still be searchable. Older runs are not automatically fetched again.
- **[Gen 2] bbox normalization**: unified to the 0-1000 convention on write. Legacy records use heuristic detection (pipeline [0,1000] / VLM [0,1]×1000); new records are mapped from StructaDoc's 0-1 normalized coordinates ×1000
- **New-pipeline mapping conventions**: `page_id` = StructaDoc `pageNumber` − 1 (null → 0); `sort_index` increments within a page; `text_level` is derived from subtype `heading-N`; `text_format` = `contentFormat`

## Population Flow

```
StructaDoc Parse Run succeeded (StructaDocParseResultSync)
  ↓
GET /api/v1/parse-runs/{id}/blocks (limit=1000, paging via nextSequence)
  ↓
Delete-then-insert INSERT INTO document_parse_blocks
   For each block (ordered by sequence):
     parse_id       = current parse ID
     page_id        = block.pageNumber - 1 (null → 0)
     sort_index     = incrementing index within the page
     sequence       = block.sequence (global reading order)
     block_type     = block.type
     text_content   = block.content
     block_data     = normalized JSON of the block
     image_id       = block.assetId → document_parse_images mapping
     sub_type       = block.subtype
     text_level     = subtype heading-N → N, otherwise -1
     text_format    = block.contentFormat
     bbox_x0/y0/x1/y1 = block.boundingBox ×1000 (0-1 → 0-1000)
     score          = block.confidence
     caption        = block.caption (exact nullable string)
     footnote       = block.footnote (exact nullable string)
```

> The legacy-record population flow (MinerU Worker downloads ZIP → parses content_list.json → DocumentParseBlockService.ParseBlock) was removed with ADR-0009; historical data is kept read-only.

## Special Notes

- Has a one-to-one relationship with `document_parse_images` when `block_type='image'`, but it is not a hard DB foreign-key constraint (soft reference via `image_id`)
- Deleting a `document_parse` automatically deletes this table's rows via CASCADE
- **`sequence` backfill** (migration `20261021094612_AddDocumentStructure`): a parse whose rows all carry a distinct non-negative integer `block_data.sequence` keeps those values; every other parse (legacy MinerU rows, missing or duplicated values) is numbered `0..n-1` by `(page_id, sort_index, id)`. No other column changes.
- **Annotation upgrade** (`20261022184348_AddBlockFootnotes`): adds only nullable `footnote`, leaving older values null and retaining every other field/table. See [upgrade, reindex and rollback](../migrations.md#block-annotation-upgrade-issue-105).
