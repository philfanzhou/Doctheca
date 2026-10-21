# document_structure_nodes — Document Structure Node Table

> This file is the single source of truth for the `document_structure_nodes` table (issue #89).

## Design Purpose

A **Structure Node** is a hand-marked part of one [Document Parse](document_parses.md): an inclusive range of block [`sequence`](document_parse_blocks.md) values at one configured [structure level](structure_levels.md), for example "Unit 1 The Changing World" (a Unit) containing "Section A" (a Section). Nodes belong to one parse; a re-parse is marked again.

## Field Inventory

| Field | Type | Constraints | Default | Description |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | Node identifier; kept stable when the tree is saved again with the same id |
| `parse_id` | `UUID` | NOT NULL, FK → `document_parses(id)` ON DELETE CASCADE | | Owning parse |
| `level_id` | `UUID` | NOT NULL, FK → `structure_levels(id)` ON DELETE RESTRICT | | Configured level of the node |
| `parent_id` | `UUID` | NULL, FK → `document_structure_nodes(id)` ON DELETE CASCADE | | Parent node; NULL for a root node |
| `name` | `VARCHAR(500)` | NOT NULL | | Display name, for example "Section A" |
| `sort_order` | `INT` | NOT NULL | | Position among siblings (follows `start_sequence`) |
| `start_sequence` | `INT` | NOT NULL | | First block `sequence` of the range (inclusive) |
| `end_sequence` | `INT` | NOT NULL | | Last block `sequence` of the range (inclusive) |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | | First save of this node id |
| `updated_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | | Last save of the tree |

## Indexes

| Index | Columns | Description |
|--------|-----|------|
| `PK_document_structure_nodes` | `id` | Primary key |
| `IX_document_structure_nodes_parse_id` | `parse_id` | Tree of a parse |
| `IX_document_structure_nodes_level_id` | `level_id` | Level usage checks |
| `IX_document_structure_nodes_parent_id` | `parent_id` | Children |

## Save Rules (enforced by `DocumentStructureService`)

The whole tree of a parse is replaced by one save (`PUT /admin/document-parses/{parseId}/structure`). The save is rejected with 400 and a field-level reason, and nothing changes, unless:

- every start and end sequence exists in that parse, and start ≤ end;
- a root node uses a top level and a child node uses a level whose parent is its parent node's level;
- every level belongs to the document's structure definition;
- children lie inside their parent and siblings do not overlap.

The replacement runs in one transaction that first locks the `document_parses` row (`SELECT … FOR UPDATE`), so concurrent saves of the same tree serialize and **the last complete save wins**; a mixed tree is impossible.

## Special Notes

- Deleting a parse (or its document) deletes its nodes through CASCADE.
- Nodes reference sequences, not block ids, so a retried synchronization of the same parse run keeps them valid.
