# document_structure_audio — Structure Audio Table

> This file is the single source of truth for the `document_structure_audio` table (issue #90).

## Design Purpose

A **Structure Audio** file is an audio file imported for one [Document Parse](document_parses.md), such as an official textbook recording, optionally linked to one of that parse's [structure nodes](document_structure_nodes.md). The persistence foundation (#107) and administrator import/review (#108) are available; administrator playback (#109) is available. Import/review and read-only playback reuse this schema without a new migration. The bytes live in the S3-compatible object storage under the `audio/` prefix; this table holds the reference and the link. See the module page [StructureAudio](../../modules/StructureAudio.md).

## Field Inventory

| Field | Type | Constraints | Default | Description |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | Audio file identifier |
| `parse_id` | `UUID` | NOT NULL, FK → `document_parses(id)` ON DELETE CASCADE | | Owning parse |
| `node_id` | `UUID` | NULL, no foreign key (see below) | | Linked structure node of the same parse; NULL when unlinked |
| `source_path` | `VARCHAR(1000)` | NOT NULL, unique with `parse_id` | | Relative path of the file in the import, for example `unit1/9s01Unit1SectionA1b.mp3` (`/` separators, Unicode NFC) |
| `content_type` | `VARCHAR(100)` | NOT NULL | | `audio/mpeg`, `audio/mp4`, `audio/wav`, `audio/ogg` or `audio/flac` |
| `size_bytes` | `BIGINT` | NOT NULL | | Stored size |
| `object_key` | `VARCHAR(500)` | NOT NULL, unique | | Storage key `audio/{parseId:N}/{id:N}{extension}`; internal metadata; appears only inside an authorized temporary playback URL, never as a separate response field |
| `match_status` | `VARCHAR(32)` | NOT NULL | | Pre-fill outcome: `matched`, `folder_name_mismatch`, `no_match`, `ambiguous`, or `node_removed` (the linked node was removed by a later structure save) |
| `link_source` | `VARCHAR(16)` | NULL | | Who set the current link: `prefill`, `manual`, or NULL when nobody did |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | | Import time |
| `updated_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | | Last link change |

## Indexes

| Index | Columns | Description |
|--------|-----|------|
| `PK_document_structure_audio` | `id` | Primary key |
| `IX_document_structure_audio_parse_id_source_path` | `parse_id, source_path` (unique) | One file per path and parse; audio of a parse |
| `IX_document_structure_audio_node_id` | `node_id` | Audio of a node |
| `IX_document_structure_audio_object_key` | `object_key` (unique) | Orphan sweep lookups |

## Special Notes

- **No foreign key on `node_id`.** A structure save replaces the parse's whole node tree (delete and re-insert, keeping the ids it is sent), which an `ON DELETE` rule would see as removing every node. Instead the tree save (`DocumentStructureRepository.ReplaceAsync`) unlinks, in the same transaction, the audio of nodes the new tree no longer contains (`node_id` NULL, `match_status` `node_removed`, `link_source` NULL). Internal repository insert/link writes take the same `document_parses` row lock and check that every linked node exists.
- Deleting a parse (or its document) deletes its rows through CASCADE; the endpoints delete the stored objects after the rows (see the module page).
- Object keys contain no file name, so `source_path` never reaches the storage. Import/review calculates match status and link source. Read-only playback and node listing change no rows and persist no URLs.
