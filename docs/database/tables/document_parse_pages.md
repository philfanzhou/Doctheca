# document_parse_pages — Optional Printed Page Labels

One complete optional page set synchronized from a StructaDoc Parse Run, owned by a local parse.
Physical page numbers are keys; printed labels are opaque untrusted display text.

| Column | PostgreSQL type | Null | Meaning |
| --- | --- | --- | --- |
| `parse_id` | `uuid` | No | Local `document_parses.id`; cascade delete |
| `page_number` | `integer` | No | Positive, one-based physical page number |
| `label` | `text` | Yes | Printed label, preserved exactly; may repeat, be empty, or contain Unicode/newlines |
| `label_source` | `text` | Yes | Optional upstream label provenance, preserved exactly |

The composite primary key is `(parse_id, page_number)`. `CK_document_parse_pages_page_number`
requires `page_number > 0`. There is no surrogate ID or label uniqueness constraint.

`DocumentParsePageRepository.ReplaceAsync` validates the entire input before writing. A transaction
locks the owning parse row, deletes its old pages, inserts the complete replacement and commits.
An empty replacement still deletes old pages. Replacements of the same parse serialize; readers
see a committed complete set, and the last committed replacement wins. Other parses keep their
own page identities. Insert/cancellation failures roll back the whole replacement. A committed
replacement is truthful work even if the caller is cancelled immediately afterward; cancellation
does not undo that commit or publish a successful partial sync.

`ReadBatchAsync` accepts explicit `DocumentParsePageKey` pairs and returns only matching pairs in
one database statement. A missing row has no value; a row with a null label remains a known page.
`DocumentParsePageIdentity.FromBlock` qualifies only StructaDoc-backed parses and a normalized
root `block_data.pageNumber` integer greater than zero that agrees with the database row's
`page_id + 1`. The row owns `parse_id`; JSON alone never supplies cross-parse identity. Missing,
null, malformed, overflowing or disagreeing page numbers, and legacy parses, return unknown.
In particular, a null canonical page stored as legacy `page_id=0` cannot borrow page 1's label.

Created by `20261022160829_AddDocumentParsePages` (#102). Existing parses have no rows until an
ordinary future sync; there is no remote backfill or public response change in this slice.
Upgrade and destructive rollback are described in [migrations.md](../migrations.md).
