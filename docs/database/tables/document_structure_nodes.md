# document_structure_nodes — Document-Owned Chapters

A chapter belongs to a [tree](document_structure_trees.md) of a logical document. Its content binding is optional and independent of its name, hierarchy, sibling order and audio links.

| Field | Type | Constraints | Meaning |
| --- | --- | --- | --- |
| `id` | UUID | Primary key | Stable chapter ID |
| `document_file_id` | UUID | NOT NULL, composite tree/parse ownership FKs | Direct document owner |
| `tree_id` | UUID | NOT NULL, same-document tree FK ON DELETE CASCADE | Lifetime tree owner |
| `parse_id` | UUID | NULL, same-document parse FK NO ACTION | Optional live content binding |
| `level_id` | UUID | NOT NULL, level FK RESTRICT | Configured level |
| `parent_id` | UUID | NULL, node FK CASCADE | Parent; service validates same-tree membership on save |
| `name` | VARCHAR(500) | NOT NULL | Display name |
| `sort_order` | INT | NOT NULL | Stable sibling order, retained when content is detached |
| `start_sequence` | INT | NULL | Inclusive first bound block sequence |
| `end_sequence` | INT | NULL | Inclusive last bound block sequence |
| `created_at`, `updated_at` | TIMESTAMPTZ | NOT NULL | Existing chapter timestamps |

`CK_document_structure_nodes_content_binding` requires either all three binding fields null, or all present with start ≤ end. The composite parse FK guarantees the parse belongs to the same document. Indexes cover parse, level, parent, `(document_file_id,parse_id)` and `(document_file_id,tree_id)`.

Existing `PUT /admin/document-parses/{parseId}/structure` remains an explicit complete binding save: validate committed levels, source endpoints, parent containment, sibling non-overlap and same-tree IDs under the document lock. Replacing a tree retains kept IDs and unlinks only explicitly removed chapters' audio in the same transaction. Unbound chapter authoring is delivered separately.

Successful new parse completion conditionally clears the current tree's content triple under persisted generation comparison. It preserves every chapter ID, parent/name/order/timestamp, the current pointer and audio links. Failed/cancelled completion changes none of that committed state. Repeated successful completion does not detach again. Parse deletion explicitly clears every binding to that parse, including history; it does not delete chapters. Document deletion owns lifetime.
