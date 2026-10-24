# document_structure_audio — Structure Audio Table

> This file is the single source of truth for the `document_structure_audio` table (issue #90).

## Design Purpose

A **Structure Audio** file is an audio file owned by a logical [document](document_files.md) and [tree](document_structure_trees.md), such as an official textbook recording, optionally linked to one of that tree's [structure nodes](document_structure_nodes.md). The persistence foundation (#107) and administrator import/review (#108) are available; administrator playback (#109) is available. Ownership migration #132 separates lifetime from content binding. The bytes live in the S3-compatible object storage under the `audio/` prefix; this table holds the reference and the link. See the module page [StructureAudio](../../modules/StructureAudio.md).

## Field Inventory

| Field | Type | Constraints | Default | Description |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | Audio file identifier |
| `document_file_id` | `UUID` | NOT NULL, same-document composite tree FK | | Direct document owner |
| `tree_id` | `UUID` | NOT NULL, tree FK ON DELETE CASCADE | | Lifetime tree owner |
| `parse_id` | `UUID` | NULL, no live-parse FK | | Immutable legacy stored object-key provenance, retained after parse deletion; nullable for later document-key imports without a fake parse |
| `node_id` | `UUID` | NULL, no foreign key (see below) | | Linked structure node of the same document/tree; NULL when unlinked |
| `source_path` | `VARCHAR(1000)` | NOT NULL, unique with `tree_id` | | Relative path of the file in the import, for example `unit1/9s01Unit1SectionA1b.mp3` (`/` separators, Unicode NFC) |
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
| `IX_document_structure_audio_tree_id_source_path` | `tree_id, source_path` (unique) | One path per tree; historical duplicate paths remain separate |
| `IX_document_structure_audio_document_file_id_tree_id` | `document_file_id, tree_id` | Owner lookup |
| `IX_document_structure_audio_node_id` | `node_id` | Audio of a node |
| `IX_document_structure_audio_object_key` | `object_key` (unique) | Orphan sweep lookups |

## Special Notes

- **No foreign key on `node_id`.** A structure save replaces the tree's complete node set (delete and re-insert, keeping the ids it is sent), which an `ON DELETE` rule would see as removing every node. Instead the tree save (`DocumentStructureRepository.ReplaceAsync`) unlinks, in the same transaction, the audio of nodes the new tree no longer contains (`node_id` NULL, `match_status` `node_removed`, `link_source` NULL). Internal repository insert/link writes take the same `document_files` row lock and check that every linked node exists.
- Parse deletion retains rows, links and bytes. Document deletion gathers exact owned keys across all current/historical trees in its metadata transaction, then cleans only keys confirmed unreferenced under per-key advisory leases. Explicit removal, upload compensation and orphan recovery share those leases/reference checks. No parse-prefix sweep is used.
- Object keys contain no file name, so `source_path` never reaches the storage. Import/review calculates match status and link source. Read-only playback and node listing change no rows and persist no URLs.
