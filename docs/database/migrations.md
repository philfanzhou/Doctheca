# Migration History

The startup composition uses the official same-version ServiceMantle 0.3.3
PostgreSQL deployment declaration and option preset, plus the direct gate service registration.
The gate is invoked by the existing startup code, without a second hosted gate. `Database:AllowCreate`
still defaults to false; the multi-instance lease and both thirty-second budgets are unchanged.
The shared single-instance identity uses a credential-free SHA-256 TCP target digest rather than
the former local string. This service always selects multi-instance coordination, so that identity
change does not affect its migrations. No schema, migration, configuration key or deployment change
is required; application rollback also restores the preceding package version.

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

The host uses the official ServiceMantle **0.3.3** `StartupDatabaseGate` direct entry once, after LLM initialization and before OpenSearch initialization. Its shared `StartupDatabaseReceipt` is the sole process-local startup observation. The scoped shared `EfCoreHealthSnapshotSource<DocthecaDbContext>` runs zero-row mapped-schema probes only after that receipt succeeds; the HTTP response contract and 3-second budget remain unchanged.

Startup sequence (all observed through the host's shutdown token):

1. **Deployment validation** — the library validates the consumer's PostgreSQL capability
   declaration for fixed MultiInstance mode without database I/O. Target observation then
   verifies reachability and identity through the shared PostgreSQL provider; authentication,
   permission, or identity failures never fall back to creation.
2. **Target preparation** — a verifiably missing database is created only when
   `Database:AllowCreate` (environment form `Database__AllowCreate`) is explicitly `true`. By
   default a missing database refuses startup with the fixed error code
   `database_target_preparation.creation_not_allowed` and writes nothing — neither the catalog nor any table.
   Creation uses a maintenance connection string that is a copy of the target connection string
   with only the database changed to `postgres` (no additional credentials), a fixed 30-second
   budget, and a post-creation re-observation that must report the target connectable.
3. **Migration orchestration** — acquire the advisory lock (fixed 30-second acquire budget), run
   the executor's read-only inspection, execute when required, and re-inspect under the lock; the
   final inspection must report `CurrentVersionCompatible` before success is published.

| Database state at startup | Handling | Result |
|---------------------------|----------|--------|
| Empty (no business tables, no history; a missing catalog counts too) | Apply every known migration | All tables + indexes + FKs + trigger, empty data, history = every known migration |
| Earlier known version (history is a strict prefix of the known migrations, that version's tables verify exactly, no table of a later migration exists) | Apply the pending migrations with EF Core | Startup succeeds; existing data kept, new columns backfilled as each migration defines |
| Full legacy database (all four tables verify against the baseline, no history) | Safe backfills + register the baseline, then apply the later migrations | Startup succeeds; baseline data unchanged |
| Legacy database with missing nullable/defaulted columns or missing model indexes | Backfill those columns/indexes, register the baseline, then apply the later migrations | Startup succeeds; existing data preserved |
| Unknown or conflicting structure (extra or wrong-typed columns, nullability mismatch, missing NOT NULL column without a default, constraint mismatch, partial table set, history claiming a version the schema contradicts, a history that is not a prefix of the known migrations, tables of a later migration without its history, or unknown migration ids) | Refuse | Startup fails with the fixed error code `DOCTHECA_DB_SCHEMA_INCOMPATIBLE` (executor level) or the orchestrator's `migration.inspection_failed` / `migration.version_too_new` / `migration.final_state_invalid` safe codes; nothing is written, nothing is auto-repaired |

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
  `database_target_preparation.creation_not_allowed` instead of being created implicitly.
- **Multi-instance upgrade**: the advisory lock serializes concurrent starts of the new version.
  During an upgrade window, stop the old (lock-free) version first — do not mix old and new
  versions against the same database.

## ServiceMantle 0.3.0 Compatibility

ServiceMantle 0.3.0 introduced the following operational classifications; all current direct package references use the same official 0.3.3. There is no schema or business-data migration in this upgrade. Missing targets now refuse with `database_target_preparation.creation_not_allowed` (previously `DOCTHECA_DB_CREATION_NOT_ALLOWED`); invalid `Database:AllowCreate` values now refuse with `database_target_preparation.invalid_target` (previously `DOCTHECA_DB_ALLOW_CREATE_INVALID`), before any database I/O and without echoing the value. Update alerts accordingly. All other startup failures retain shared allow-listed codes.

The executor now consumes the shared schema primitives described below; classification, migration history,
takeover/backfill rules and the PostgreSQL session advisory lock retain their original behavior. Roll back by deploying the previous image and package versions against the same existing compatible database and restoring old alert codes. No rollback SQL is needed. An already created database or committed migration remains after later failure/cancellation. A cancelled gate can leave its shared receipt Running while the process exits; it never reports success.

## Shared schema evidence and product adaptation

`ServiceMantle.Database.PostgreSql` and `ServiceMantle.Persistence.Relational` use official
**0.3.3**, matching every other direct ServiceMantle reference in the repository.
`PostgreSqlSchemaEvidenceReader` explicitly reads extended names, expression key counts and
INCLUDE columns for exactly the four public business tables. `EfCoreExpectedSchemaDerivation`
uses the EF design-time model with extended evidence. `SchemaEvidenceComparer` performs neutral
comparison; `EfCoreMigrationBaselineWriter` still owns the independent parameterized transaction.
There is no local catalog query, alternate snapshot, or model-gap fallback. The executor retains
only product policy and the metadata needed for the original safe backfill DDL. The executor still chooses which migration to stamp and when: read-only inspection, fresh inspection immediately before execution, safe backfills, then history registration.

- The explicitly public history SELECT remains a product boundary: the shared reader's history
  lookup follows search_path, while another schema's same-named table must not claim the public
  baseline. Public unknown ids retain their original priority over incompatible mapped shapes.
  Table presence now comes entirely from the shared exact-scope snapshot.
- Required names still correlate with OrdinalIgnoreCase before individual comparisons; shared
  names are normalized only after that pairing. Swapped required names cannot match another
  object's shape. Extra indexes and unrelated tables remain ignored; extra foreign keys refuse.
- Index key counts and INCLUDE evidence enter the shared comparer explicitly. Wrong required
  expressions, INCLUDE columns, order and uniqueness still refuse; only a missing required
  index may collect the original legacy backfill. Current history never permits that backfill.
- Identity is normalized to None and referenced FK schema is omitted from comparison, preserving
  the original dimensions. Stored defaults are not compared; expected default values remain
  only for the original nullable/defaulted-column backfill. Physical column order is not a new
  promise. Empty/partial/current/legacy/unknown-history states and the updated_at trigger DDL
  retain their existing rules.
- Permission/network/incomplete evidence fails closed; no expected columns are invented as
  actual facts. Caller cancellation remains cancellation, including after connection cleanup;
  committed DDL/history remains truthful pending state rather than being compensated or
  reported as success. The caller still owns the advisory lease and write-before recheck;
  the reader does not provide a cross-query consistent snapshot.

| Shared fact/difference | Product decision |
| --- | --- |
| TargetDatabaseMissing | Empty; the Host creation gate keeps its existing AllowCreate policy. |
| ReadFailed / incomplete evidence | InspectionFailed; no local fallback. |
| MissingTable | No business table/history is Empty; other missing/partial tables refuse, including current history. |
| ExtraTable | Filtered outside the four public tables; ignored. |
| MissingColumn | Current history refuses; legacy collects only the original nullable/defaulted safe DDL. |
| ExtraColumn | Refuse on mapped tables. |
| ColumnTypeMismatch / ColumnNullabilityMismatch | Refuse using exact provider type strings and nullability. |
| ColumnIdentityMismatch | Ignored; Identity is normalized to None on both sides. |
| PrimaryKeyMismatch | Refuse missing/wrong ordered columns; real name additionally must match ignoring case. |
| ForeignKeyMismatch | Refuse missing/wrong named shape/delete rule or unexpected FK count; referenced schema is not an added dimension. |
| IndexMismatch | Missing required name is safely backfilled only in legacy; current history or an existing wrong shape/unique flag refuses. Extra indexes are ignored. |
| Expression / INCLUDE / KeyCount facts | Actual required name is checked using its real mapping/count; wrong shapes refuse, extra indexes remain ignored. |

There is no new schema, model, migration, HTTP API, configuration key or deployment port.
The writer produces the original history column types, primary-key name, migration ID and EF
product version. It owns its transaction directly; no nested EF transaction wraps it.
Already committed backfill DDL may remain after cancellation or a later stamp failure; the next
startup rereads that truthful pending state. Shared writer idempotence does not replace the
Host's advisory lease and does not promise lock-free concurrent stamp success.

Rollback restores the previous image against the same database, after stopping the newer
instance. No rollback SQL is required; keep the existing prohibition on mixing old lock-free
and new locking versions. The real PostgreSQL shape matrix was run against both executors,
including naming/case, index order/unique/expression/INCLUDE, extra/unrelated duplicate shapes,
Identity, missing columns/indexes and history priority. PostgreSQL statement statistics verify
that both ordinary and duplicate-shape reads come from the shared reader, with zero local
column adaptation queries. Cancellation/permission/writer failures and history shape have separate
integration assertions. Run the container contract and actual old-image acceptance with:

```bash
python3 scripts/verify-container-startup.py doctheca:current doctheca:previous
```

With the optional second image, the script verifies the separate destructive down-maintenance
path, not backup restoration. It works in its own isolated test database migrated by
the current image: it applies the documented down script of the newest migration, starts the
previous image (schema, history and business-row counts must stay exactly the same), then applies
the earlier down script back to the baseline, removes only the verified history table and starts the
current image again, which must take the baseline over through the shared stamp and migrate it
forward with business rows unchanged. Synthetic credentials
remain in temporary mode-0600 environment files and are checked for disclosure without printing.

## Current Database Version

| Migration | Contents |
|-----------|----------|
| `20260930161548_InitialCreate` | All four tables, their indexes and foreign keys, and the `set_document_files_updated_at` trigger — the production structure as of the StructaDoc migration (ADR-0009) |
| `20261021094612_AddDocumentStructure` | Nullable `document_parse_blocks.sequence` (backfilled) with the unique index `(parse_id, sequence)`; nullable `document_files.structure_definition_id`; the `structure_definitions`, `structure_levels`, and `document_structure_nodes` tables (issue #89) |
| `20261021153535_AddStructureAudio` | The `document_structure_audio` table with its indexes and the cascade foreign key to `document_parses` (issue #90); no existing table changes |
| `20261022160829_AddDocumentParsePages` | The `document_parse_pages` table with composite physical-page key and cascade parse ownership (#102); no existing table or row changes |

Adding or changing migrations requires extending `DocthecaMigrationExecutor.KnownMigrationIds`
(the known-version contract) and `TablesAfterMigration` together with the executor's takeover
rules. An earlier version is verified against that migration's own target model (its Designer
snapshot), never against the current model, so every known version stays upgradeable in place.

## Document Structure Upgrade (issue #89)

The historical procedures below target their named migration. From a later version, apply
later documented down scripts first, newest to oldest; never leave a gap in migration history.

**Upgrade.** Starting the new image on a database at `20260930161548_InitialCreate` classifies it as
pending (`PendingMigration` for the orchestrator) and applies `20261021094612_AddDocumentStructure`
in one EF Core migration transaction under the advisory lock:

1. add the nullable `document_parse_blocks.sequence` and `document_files.structure_definition_id`;
2. backfill `sequence` for every existing block: a parse whose rows all carry a distinct
   non-negative integer `block_data.sequence` (StructaDoc-synced rows) keeps those values; every
   other parse is numbered `0..n-1` by `(page_id, sort_index, id)`;
3. create the three structure tables, the unique index `(parse_id, sequence)`, and the foreign keys.

No existing row changes otherwise (verified by `StructureMigrationTests` with populated data). The
backfill is one `UPDATE` per numbering rule; on large block tables expect it to take roughly as long
as rewriting the table once.

**OpenSearch.** The index mapping moves to version 3 (`structure_node_ids`, `structure_path`). As
for every mapping step, the first start of the new image deletes and recreates the index, so search
answers no hits until the parses are re-indexed. After the upgrade, call
`POST /admin/search-index/reindex` once (or click "重建检索索引" on the parse results page).

**Rollback.** The previous image refuses a history that contains a migration id it does not know
(`migration.version_too_new`) and refuses extra columns on its four tables, so it cannot run
against the migrated database as is. To roll back:

1. stop every instance of the new image and back up the database;
2. run [rollback/20261021094612_AddDocumentStructure.down.sql](rollback/20261021094612_AddDocumentStructure.down.sql)
   (it drops the structure tables, the two new columns and their indexes, and removes the history
   row; all structure definitions and marked nodes are lost, business rows are kept);
3. start the previous image — the database is then exactly its baseline (verified by
   `StructureMigrationTests.RollbackScript_RestoresTheExactBaselineAndKeepsBusinessRows`);
4. re-index search from the previous image if needed (its mapping version 2 index is recreated on
   start because the stored version 3 differs).

## Structure Audio Upgrade (issue #90)

Import/review (#108) and administrator playback (#109) reuse this schema without additional migrations. Playback reads write no state or URLs. Reverting only playback restores the earlier image/configuration while retaining audio data; recovery to before the audio migration uses the verified backup/restore procedure below.

**Upgrade.** Starting the new image on a database at `20261021094612_AddDocumentStructure` (or
earlier) classifies it as pending and applies `20261021153535_AddStructureAudio` under the advisory
lock: it only creates the `document_structure_audio` table, its three indexes and its foreign key to
`document_parses`. No existing table, column or row changes (verified by
`AudioMigrationTests.PopulatedStructureVersion_IsMigratedInPlace_KeepingEveryRow`), and the
OpenSearch mapping is unchanged.

**Standard recovery to the previous version.** Follow the current project's backup/restore model,
like [SignaCore at 74d263bb](https://github.com/philfanzhou/SignaCore/blob/74d263bbb98b63d86bc47808b45c580936edb6e8/docs/database/migrations.md).
The previous image is not required to understand a newer database schema; it may fail closed on
an unknown migration ID (`migration.version_too_new`). Switching images does not run `Down`:

1. **Before upgrading**, stop writers and take a consistent PostgreSQL backup. Verify that it can
   be restored. Retain the matching image/configuration and an object-storage backup or recovery
   set; protect configuration backups as secrets. Do not print connection strings or keys.
2. To recover the previous version, stop **every** new-version instance, preserve the current
   state separately if later recovery is needed, and restore the pre-upgrade database and its
   corresponding image/configuration/storage set.
3. Start the previous image and verify health and representative document/structure reads.
   Writes made after the backup are lost; restoring the old database cannot retain their rows.
   Storage restored to another point in time may leave missing or unreferenced objects, so restore
   the matching storage set rather than guessing which objects to remove.

**Optional destructive maintenance, not the standard recovery.** If an operator explicitly
chooses to discard all audio metadata while retaining every other current row, stop all instances
and take another verified backup, then run
[rollback/20261021153535_AddStructureAudio.down.sql](rollback/20261021153535_AddStructureAudio.down.sql).
It drops only `document_structure_audio` and that migration's history row. It does not restore
backed-up data or remove stored audio objects; use storage tools to clean only verified abandoned
`audio/` objects after accounting for retained backups. The previous image does not read the
prefix. `AudioMigrationTests.DestructiveDownScript_RestoresTheExactStructureVersionAndKeepsEveryOtherRow`
and the optional previous-image container check verify this maintenance path, not the standard
backup restoration or compatibility of the previous image with the upgraded schema.

## Printed Page Label Upgrade (issue #102)

**Upgrade.** Back up the database before deploying. Startup applies
`20261022160829_AddDocumentParsePages` under the existing advisory lock. It creates only the
page table, its composite primary key, positive-page constraint and cascading parse foreign key.
Every earlier file, parse, Block, image, structure and audio row stays unchanged. The executor
keeps separate table lists and target models for all historical versions, including the audio
version. A later table without history, a conflicting current shape or unknown history is refused.
Old parses have no labels and remain readable; there is no remote re-sync/backfill. HTTP fields,
OpenSearch mapping version 3, authorization, storage, configuration and port 5012 are unchanged.

**Rollback.** The previous image refuses the new history ID; changing only the image does not run
EF `Down`. Stop every instance and take a verified backup. Then run
[rollback/20261022160829_AddDocumentParsePages.down.sql](rollback/20261022160829_AddDocumentParsePages.down.sql)
before starting the previous image. This deliberate maintenance drops only pages and their
migration history row, losing all printed labels while retaining every other row and all stored
objects. `PageMigrationTests` verifies populated upgrade, exact historical schema/history after
the down script, unchanged earlier rows and forward migration again. Standard full recovery
instead restores the verified pre-upgrade backup and its matching image/configuration/storage;
post-backup writes cannot survive that restoration. Roll back any later migrations first, in
reverse order, before applying an older down script.

## Change Log

| Date | Change | Impact |
|------|------|------|
| 2026-10-23 | Added `20261022160829_AddDocumentParsePages` (#102) | Additive optional page labels; previous image requires backup/down-script maintenance or pre-upgrade backup restoration |
| 2026-10-22 | Added migration `20261021153535_AddStructureAudio` (issue #90) | Additive table only; standard recovery restores the pre-upgrade backup; the separate down script discards audio metadata |
| 2026-10-21 | Added migration `20261021094612_AddDocumentStructure` (issue #89) and taught the executor to apply pending known migrations, verifying earlier versions against their own target model | Additive schema; `sequence` backfilled for every block; rollback requires the documented down script before the previous image starts |
| 2026-10-06 | Adopted extended shared exact-scope evidence; removed all local catalog adaptation and redundant evidence types | Original decisions/backfill/public history/lease retained; no schema change |
| 2026-10-04 | Adopted shared schema reader/derivation/comparer/baseline writer with bounded PostgreSQL naming/model-gap adaptation | Original classification/backfill/history/lease preserved; no schema or configuration change; same-database rollback verified |
| 2026-10-02 | Adopted formal ServiceMantle 0.3.0 shared direct startup gate, receipt, and EF Core health snapshot source; removed local duplicate algorithms | Only the two operational refusal codes above change; no schema/data/configuration/HTTP changes; old images can roll back against the same compatible existing database |
| 2026-09-30 | Startup migration delegated to ServiceMantle: deployment validation, explicit `Database:AllowCreate` target preparation (default refuse), and advisory-lock orchestration of multi-instance startup | Missing databases no longer get created implicitly (`DOCTHECA_DB_CREATION_NOT_ALLOWED` by default); concurrent instances serialize on one advisory lock and only one executes the migration |
| 2026-09-30 | Introduced the EF migration baseline and the verified legacy-takeover executor; retired `EnsureCreated` and the handwritten ALTER list | Schema becomes checkable (`__EFMigrationsHistory`); unknown structures now fail startup instead of being silently patched |
| 2026-09-21 | Added document_files.structadoc_document_id and document_parses.structadoc_parse_run_id; file_path made nullable | StructaDoc parse pipeline migration (ADR-0009) — folded into the baseline |
| 2026-07-04 | Added document_files.subject/grade/year columns | Document metadata analysis feature — folded into the baseline |
| 2026-07-04 | Added document_parses.content_list_v2 / model_json / layout_json columns | MinerU pipeline/vlm structured output — folded into the baseline |
| 2026-07-04 | Added document_parse_blocks and document_parse_images tables | OpenSearch block-level indexing and image management — folded into the baseline |
| 2026-07-04 | Dropped document_parses.layout_pdf_path | Replaced by layout_json — folded into the baseline |
| 2026-07-04 | Added document_parses.model_version column | vlm / pipeline dual model versions — folded into the baseline |
