# document_structure_trees — Document-Owned Chapter Trees

A real persistent tree belongs to one logical [document](document_files.md). It does not represent a parse or create a parse job.

| Field | Type | Constraints | Meaning |
| --- | --- | --- | --- |
| `id` | UUID | Primary key | Stable tree identity |
| `document_file_id` | UUID | NOT NULL, document FK ON DELETE CASCADE | Direct lifetime owner |
| `origin_parse_id` | UUID | NULL, unique; no live-parse FK | Immutable legacy/first-save provenance |

The alternate key `(document_file_id,id)` supports same-document composite FKs from nodes/audio and the document's nullable current pointer. Migration creates a distinct tree for each legacy parse containing nodes or audio, including audio-only history. It preserves all row IDs and object keys. Origin provenance remains after parse deletion and resolves historical parse compatibility reads deterministically.

The initial current tree is selected once: parsed, nonempty legacy trees; ParsedAt descending with nulls last, then parse UUID descending. Later parse status/timestamp changes and successful reparse never select a different tree. Existing parse-scoped explicit structure saves resolve the origin tree first, otherwise the current tree. A first explicit save may create the initial tree. New content-free authoring routes are a separate feature.

See [ownership migration and recovery](../migrations.md#document-owned-structure-132).
