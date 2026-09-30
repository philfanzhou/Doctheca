# Migration History

## Migration Strategy

The database is managed with **EF Core migrations**. The single baseline migration
`20260930161548_InitialCreate` (see [Migrations](../../src/Database/Migrations/)) creates the four
business tables — `document_files`, `document_parses`, `document_parse_images`,
`document_parse_blocks` — with their columns, types, primary and foreign keys, indexes, and the
`set_document_files_updated_at` trigger, all matching the production structure the previous raw-SQL
initializer produced.

At startup `DocthecaMigrationExecutor` ([source](../../src/Database/DocthecaMigrationExecutor.cs))
classifies the target database and executes. The executor implements ServiceMantle's
`IDatabaseMigrationExecutor`: its internal states map onto the shared observation states
(`LegacyTakeoverRequired` → `PendingMigration`, the rest one-to-one), and the shared
`DatabaseMigrationOrchestrator` runs the whole sequence under a PostgreSQL session advisory lock
(session-keyed by the `doctheca` service id) so multiple instances starting against the same
database serialize — only one executes the migration, the others wait for the lock, re-read
`CurrentVersionCompatible`, and skip.

Startup sequence (three phases, all observed through the host's shutdown token):

1. **Deployment validation** — the ServiceMantle PostgreSQL provider observes the resolved
   connection string (server reachability, identity, product). Unreachable servers, authentication
   or permission failures, and identity conflicts refuse startup with the provider's safe
   `database_target_preparation.*` codes; they are never reinterpreted as a missing database.
2. **Target preparation** — a verifiably missing database is created only when
   `Database:AllowCreate` (environment form `Database__AllowCreate`) is explicitly `true`. By
   default a missing database refuses startup with the fixed error code
   `DOCTHECA_DB_CREATION_NOT_ALLOWED` and writes nothing — neither the catalog nor any table.
   Creation uses a maintenance connection string that is a copy of the target connection string
   with only the database changed to `postgres` (no additional credentials), a fixed 30-second
   budget, and a post-creation re-observation that must report the target connectable.
3. **Migration orchestration** — acquire the advisory lock (fixed 30-second acquire budget), run
   the executor's read-only inspection, execute when required, and re-inspect under the lock; the
   final inspection must report `CurrentVersionCompatible` before success is published.

| Database state at startup | Handling | Result |
|---------------------------|----------|--------|
| Empty (no business tables, no history; a missing catalog counts too) | Apply the baseline migration | The four tables + indexes + FKs + trigger, empty data, history = baseline |
| Full legacy database (all four tables verify against the baseline, no history) | Safe backfills + register the baseline | Startup succeeds; schema and data unchanged |
| Legacy database with missing nullable/defaulted columns or missing model indexes | Backfill those columns/indexes, then register the baseline | Startup succeeds; existing data preserved |
| Unknown or conflicting structure (extra or wrong-typed columns, nullability mismatch, missing NOT NULL column without a default, constraint mismatch, partial table set, history claiming a version the schema contradicts, or unknown migration ids) | Refuse | Startup fails with the fixed error code `DOCTHECA_DB_SCHEMA_INCOMPATIBLE` (executor level) or the orchestrator's `migration.inspection_failed` / `migration.version_too_new` / `migration.final_state_invalid` safe codes; nothing is written, nothing is auto-repaired |

The registration ("stamp") of the baseline happens only after the structure verification passes,
inside one parameterized transaction. Refused states fail startup; logs and errors carry safe
error codes and schema identifiers only, never SQL statements, connection values, or driver
details. Back up the database before upgrading; reconcile refused databases manually.

### Orchestration failure codes

| Code | Meaning |
|------|---------|
| `migration.lock_timeout` | The advisory lock could not be acquired within the fixed 30-second budget (another instance holds it) |
| `migration.lock_failed` | The advisory lease was lost while held |
| `migration.inspection_failed` | The target structure could not be classified |
| `migration.version_too_new` | The history contains migration ids this application does not know |
| `migration.execution_failed` | The executor threw while executing |
| `migration.final_state_invalid` | The post-execution inspection did not report `CurrentVersionCompatible` |

All of them exit the process with a non-zero code; the advisory lock is always released before the
refusal. Cancellation (host shutdown) stops before new phases begin; already committed DDL/history
stays as a truthful pending state that the next start re-reads under the lock — there is no
rollback and no synthesized success.

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
- **Missing-database upgrade step**: deployments that relied on EF Core `Migrate` implicitly
  creating the `doctheca` database must either create it manually before upgrading or set
  `Database__AllowCreate=true` (the migration account then needs CREATEDB and access to the
  `postgres` maintenance database). By default a missing database now refuses startup with
  `DOCTHECA_DB_CREATION_NOT_ALLOWED` instead of being created implicitly.
- **Multi-instance upgrade**: the advisory lock serializes concurrent starts of the new version.
  During an upgrade window, stop the old (lock-free) version first — do not mix old and new
  versions against the same database.

## Current Database Version

| Migration | Contents |
|-----------|----------|
| `20260930161548_InitialCreate` | All four tables, their indexes and foreign keys, and the `set_document_files_updated_at` trigger — the production structure as of the StructaDoc migration (ADR-0009) |

Adding or changing migrations requires extending `DocthecaMigrationExecutor.KnownMigrationIds`
(the known-version contract) together with the executor's takeover rules.

## Change Log

| Date | Change | Impact |
|------|------|------|
| 2026-09-30 | Startup migration delegated to ServiceMantle: deployment validation, explicit `Database:AllowCreate` target preparation (default refuse), and advisory-lock orchestration of multi-instance startup | Missing databases no longer get created implicitly (`DOCTHECA_DB_CREATION_NOT_ALLOWED` by default); concurrent instances serialize on one advisory lock and only one executes the migration |
| 2026-09-30 | Introduced the EF migration baseline and the verified legacy-takeover executor; retired `EnsureCreated` and the handwritten ALTER list | Schema becomes checkable (`__EFMigrationsHistory`); unknown structures now fail startup instead of being silently patched |
| 2026-09-21 | Added document_files.structadoc_document_id and document_parses.structadoc_parse_run_id; file_path made nullable | StructaDoc parse pipeline migration (ADR-0009) — folded into the baseline |
| 2026-07-04 | Added document_files.subject/grade/year columns | Document metadata analysis feature — folded into the baseline |
| 2026-07-04 | Added document_parses.content_list_v2 / model_json / layout_json columns | MinerU pipeline/vlm structured output — folded into the baseline |
| 2026-07-04 | Added document_parse_blocks and document_parse_images tables | OpenSearch block-level indexing and image management — folded into the baseline |
| 2026-07-04 | Dropped document_parses.layout_pdf_path | Replaced by layout_json — folded into the baseline |
| 2026-07-04 | Added document_parses.model_version column | vlm / pipeline dual model versions — folded into the baseline |
