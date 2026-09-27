# document_parse_images — Document Parse Image Table

> This file is the single source of truth for the `document_parse_images` table.

## Field Inventory

| Field | Type | Constraints | Default | Description |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | Image unique identifier |
| `parse_id` | `UUID` | NOT NULL, FK → `document_parses(id)` ON DELETE CASCADE | | Associated parse |
| `image_name` | `VARCHAR(200)` | NOT NULL | | Image file name (legacy: original file name inside the MinerU ZIP; new: StructaDoc Asset display name) |
| `image_path` | `VARCHAR(500)` | NOT NULL | | Dual semantics: legacy parses = OSS object path; new parses (parse has `structadoc_parse_run_id`) = StructaDoc Asset ID (Guid string) |
| `content_type` | `VARCHAR(50)` | NOT NULL | `'image/jpeg'` | MIME type (image/jpeg or image/png) |

## Indexes

| Index | Columns | Description |
|--------|-----|------|
| `PK_document_parse_images` | `id` | Primary key |
| `IX_document_parse_images_parse_id` | `parse_id` | Query all images by parse |

## Special Notes

- **`image_name`**: keeps the original file name from the parse artifacts (e.g. `img_in_image_box_15_3.png`) for debugging and traceability; it is also the matching key for the Markdown relative path `images/<name>`
- **`image_path`**: discriminated by the owning parse — OSS path for legacy parses; StructaDoc Asset ID for new parses, with bytes streamed from StructaDoc via the proxy endpoint `GET /admin/document-parses/{parseId}/images/{imageId}/content` (ADR-0009)
- **Relationship with `document_parse_blocks.image_id`**: when `block_type='image'`, `document_parse_blocks.image_id` points to this table
- Deleting a `document_parse` automatically deletes these rows via CASCADE; deleting a `document_parse_image` sets `document_parse_blocks.image_id` to NULL
