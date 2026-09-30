# Migration History

## Migration Strategy

The database is managed with **EF Core migrations**. The single baseline migration
`20260930161548_InitialCreate` (see [Migrations](../../src/Database/Migrations/)) creates the four
business tables — `document_files`, `document_parses`, `document_parse_images`,
`document_parse_blocks` — with their columns, types, primary and foreign keys, indexes, and the
`set_document_files_updated_at` trigger, all matching the production structure the previous raw-SQL
initializer produced.

At startup `DocthecaMigrationExecutor` ([source](../../src/Database/DocthecaMigrationExecutor.cs))
classifies the target database and executes:

| Database state at startup | Handling | Result |
|---------------------------|----------|--------|
| Empty (no business tables, no history; a missing catalog counts too) | Apply the baseline migration | The four tables + indexes + FKs + trigger, empty data, history = baseline |
| Full legacy database (all four tables verify against the baseline, no history) | Safe backfills + register the baseline | Startup succeeds; schema and data unchanged |
| Legacy database with missing nullable/defaulted columns or missing model indexes | Backfill those columns/indexes, then register the baseline | Startup succeeds; existing data preserved |
| Unknown or conflicting structure (extra or wrong-typed columns, nullability mismatch, missing NOT NULL column without a default, constraint mismatch, partial table set, history claiming a version the schema contradicts, or unknown migration ids) | Refuse | Startup fails with the fixed error code `DOCTHECA_DB_SCHEMA_INCOMPATIBLE` and a structure-difference summary; nothing is written, nothing is auto-repaired |

The registration ("stamp") of the baseline happens only after the structure verification passes,
inside one parameterized transaction. Refused states fail startup; logs and errors carry schema
identifiers only, never SQL statements or connection values. Back up the database before upgrading;
reconcile refused databases manually.

## Legacy Upgrade Notes

- Databases created by the retired `EnsureCreated` + `ALTER TABLE` initializer are taken over
  automatically on the first start of the new version: the structure is verified, the updated_at
  trigger and any missing model indexes are (re-)created idempotently, and the baseline is
  registered. Business data is preserved.
- Rollback: redeploying the previous image keeps working against a stamped database — the old
  initializer treats "no pending migrations + tables present" as up to date and does not modify it.
- The retired code paths (`EnsureCreated` fallback and the handwritten `ALTER TABLE` list in
  `DatabaseInitializer`) have been deleted; the shared `Common/Database/DatabaseInitializer` copy
  is no longer used by this repository.

## Current Database Version

| Migration | Contents |
|-----------|----------|
| `20260930161548_InitialCreate` | All four tables, their indexes and foreign keys, and the `set_document_files_updated_at` trigger — the production structure as of the StructaDoc migration (ADR-0009) |

Adding or changing migrations requires extending `DocthecaMigrationExecutor.KnownMigrationIds`
(the known-version contract) together with the executor's takeover rules.

## Change Log

| Date | Change | Impact |
|------|------|------|
| 2026-09-30 | Introduced the EF migration baseline and the verified legacy-takeover executor; retired `EnsureCreated` and the handwritten ALTER list | Schema becomes checkable (`__EFMigrationsHistory`); unknown structures now fail startup instead of being silently patched |
| 2026-09-21 | Added document_files.structadoc_document_id and document_parses.structadoc_parse_run_id; file_path made nullable | StructaDoc parse pipeline migration (ADR-0009) — folded into the baseline |
| 2026-07-04 | Added document_files.subject/grade/year columns | Document metadata analysis feature — folded into the baseline |
| 2026-07-04 | Added document_parses.content_list_v2 / model_json / layout_json columns | MinerU pipeline/vlm structured output — folded into the baseline |
| 2026-07-04 | Added document_parse_blocks and document_parse_images tables | OpenSearch block-level indexing and image management — folded into the baseline |
| 2026-07-04 | Dropped document_parses.layout_pdf_path | Replaced by layout_json — folded into the baseline |
| 2026-07-04 | Added document_parses.model_version column | vlm / pipeline dual model versions — folded into the baseline |
