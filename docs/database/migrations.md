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

The host uses the formal ServiceMantle **0.3.0** `StartupDatabaseGate` direct entry once, after LLM initialization and before OpenSearch initialization. Its shared `StartupDatabaseReceipt` is the sole process-local startup observation. The scoped shared `EfCoreHealthSnapshotSource<DocthecaDbContext>` runs zero-row mapped-schema probes only after that receipt succeeds; the HTTP response contract and 3-second budget remain unchanged.

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
  `database_target_preparation.creation_not_allowed` instead of being created implicitly.
- **Multi-instance upgrade**: the advisory lock serializes concurrent starts of the new version.
  During an upgrade window, stop the old (lock-free) version first — do not mix old and new
  versions against the same database.

## ServiceMantle 0.3.0 Compatibility

All direct ServiceMantle package references use formal 0.3.0. There is no schema or business-data migration in this upgrade. Missing targets now refuse with `database_target_preparation.creation_not_allowed` (previously `DOCTHECA_DB_CREATION_NOT_ALLOWED`); invalid `Database:AllowCreate` values now refuse with `database_target_preparation.invalid_target` (previously `DOCTHECA_DB_ALLOW_CREATE_INVALID`), before any database I/O and without echoing the value. Update alerts accordingly. All other startup failures retain shared allow-listed codes.

The executor now consumes the shared schema primitives described below; classification, migration history,
takeover/backfill rules and the PostgreSQL session advisory lock retain their original behavior. Roll back by deploying the previous image and package versions against the same existing compatible database and restoring old alert codes. No rollback SQL is needed. An already created database or committed migration remains after later failure/cancellation. A cancelled gate can leave its shared receipt Running while the process exits; it never reports success.

## Shared schema evidence and product adaptation

`ServiceMantle.Database.PostgreSql` and `ServiceMantle.Persistence.Relational` are direct formal
**0.3.0** references in the Database project, matching the rest of the repository.
`PostgreSqlSchemaEvidenceReader` supplies ordinary actual column facts;
`EfCoreExpectedSchemaDerivation` supplies the neutral EF expectation;
`SchemaEvidenceComparer` performs every neutral comparison; and
`EfCoreMigrationBaselineWriter` owns the independent, parameterized history transaction.
The executor still chooses which migration to stamp and when: read-only inspection, fresh
inspection immediately before execution, safe backfills, then history registration.

The following local responsibilities are necessary to preserve existing decisions:

- A public-table presence query and a short, explicitly public history query preserve unknown
  migration priority, the empty/partial-table distinction, and history ownership. A same-named
  history table in another schema cannot claim the public baseline. Shared history data is not
  substituted for this product check.
- Bounded public four-table PK/FK/index facts retain actual names, ordered column associations,
  unique flags and `array_length(indkey, 1)` (including INCLUDE attributes). The shared model
  omits names/KeyCount and its reader omits expression indexes. Expected names and backfill
  defaults/DDL remain EF metadata; neutral expected shapes/types come from shared derivation.
- Required objects first correlate by their real case-insensitive names. Each named shape is
  then compared separately by the same shared comparer, so swapped index/FK names cannot be
  rescued by a different object's matching shape. Extra indexes remain ignored, extra FKs do
  not. Both sides normalize the default schema to public, ignore Identity, and omit referenced
  schema from FK comparison because the original contract compared only referenced table names.
- The ordinary path never performs the local column query. If the shared reader fails in a
  model-construction catalog step, a scalar catalog diagnostic must prove an unrepresentable
  shape (duplicate plain index/FK identities, empty tables or invalid neutral identifiers).
  Only then are actual columns reread for the public four business tables, alongside their
  bounded naming facts. The diagnostic returns only a boolean, not a second global snapshot.
  This handles extra same-shaped indexes and unrelated duplicate shapes without widening
  rejection. Permission/network failures or incomplete evidence remain InspectionFailed;
  cancellation remains cancellation. No expected columns are invented as actual facts.
- Store default values and the original safe nullable/defaulted-column and missing-index DDL,
  updated_at trigger backfill, fixed diagnostics, cancellation and advisory lease remain
  product responsibilities. Raw driver exception payloads are not attached to product logs.

| Shared fact/difference | Product decision |
| --- | --- |
| TargetDatabaseMissing | Empty; the Host creation gate keeps its existing AllowCreate policy. |
| ReadFailed / incomplete local evidence | InspectionFailed, except the proved model gap above with complete bounded facts. |
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
that ordinary column reads come from the shared reader and bounded column adaptation runs only
for the proved gap. Cancellation/permission/writer failures and history shape have separate
integration assertions. Run the container contract and actual old-image acceptance with:

```bash
python3 scripts/verify-container-startup.py doctheca:current doctheca:previous
```

With the optional second image, the script removes only the verified history table in its own
isolated test database, reruns the current image through legacy takeover/shared stamp, then
starts the previous image sequentially. Schema, history and business-row counts must match
the original baseline exactly. Synthetic credentials
remain in temporary mode-0600 environment files and are checked for disclosure without printing.

## Current Database Version

| Migration | Contents |
|-----------|----------|
| `20260930161548_InitialCreate` | All four tables, their indexes and foreign keys, and the `set_document_files_updated_at` trigger — the production structure as of the StructaDoc migration (ADR-0009) |

Adding or changing migrations requires extending `DocthecaMigrationExecutor.KnownMigrationIds`
(the known-version contract) together with the executor's takeover rules.

## Change Log

| Date | Change | Impact |
|------|------|------|
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
