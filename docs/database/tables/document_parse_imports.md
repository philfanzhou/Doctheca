# document_parse_imports — Legacy Compatibility Table

> Quaestura below is the former QuestionBank service, migrated out as an independent repository (ADR-0011). This table had already been removed from Doctheca before that migration.

`document_parse_imports` was once used by Quaestura to write import status back into Doctheca. That design could not share a database transaction with Quaestura's question writes, could not reliably prevent duplicate imports, and created cross-service data-ownership confusion.

Doctheca currently provides no Quaestura integration interface; if the integration is re-established in the future, import idempotency responsibility should belong to Quaestura:

- The Doctheca runtime no longer maps, queries, creates, or updates this table.
- `DatabaseInitializer` no longer creates this table.
- The upgrade process does not automatically execute `DROP TABLE`; the table and data in existing environments are left untouched.
- Removing the legacy table must be done through a separate backup, impact check, and explicit SQL change.

Historical fields, listed only to identify legacy data: `id`, `parse_id`, `imported_by`, `status`, `note`, `imported_question_ids`, `created_at`, `updated_at`.
