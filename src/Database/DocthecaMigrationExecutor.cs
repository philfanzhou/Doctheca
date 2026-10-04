using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Npgsql;
using ServiceMantle.Migration;
using ServiceMantle.Database.PostgreSql.Migration;
using ServiceMantle.Persistence.Relational.Migration;
using NeutralSnapshot = ServiceMantle.Migration.SchemaSnapshot;

namespace Doctheca.Database;

/// <summary>
/// The EF-migration executor for the Doctheca document library database (issue #52).
/// </summary>
/// <remarks>
/// <para>
/// Since issue #53 the executor also implements <see cref="IDatabaseMigrationExecutor"/> for the
/// shared ServiceMantle <c>DatabaseMigrationOrchestrator</c>: its internal
/// <see cref="DatabaseState"/> classification maps one-to-one onto
/// <see cref="MigrationObservationState"/> except that a verified legacy database
/// (<see cref="DatabaseState.LegacyTakeoverRequired"/>) is reported as
/// <see cref="MigrationObservationState.PendingMigration"/> — the orchestrator's "execute once
/// under the lock" signal. The interface methods are explicit implementations around the same
/// public inspection/execution entry points; the takeover, backfill, stamp, and refusal
/// semantics delivered by issue #52 are unchanged and <c>__EFMigrationsHistory</c> remains
/// owned by this executor.
/// </para>
/// <para>
/// <c>InspectAsync</c> is strictly read-only (PostgreSQL catalogs only) and classifies the target
/// database against the single known baseline migration:
/// </para>
/// <list type="bullet">
/// <item>Empty: no business tables and no applied history (a missing catalog also classifies as
/// Empty; creating it remains EF Core <c>Migrate</c>'s existing job, unchanged from before).</item>
/// <item>CurrentVersionCompatible: the baseline is applied and the complete current schema
/// verifies; nothing will be written.</item>
/// <item>LegacyTakeoverRequired: all four business tables are present without history and the
/// structure verifies — missing columns that are nullable or have a store default, missing model
/// indexes, and the updated_at trigger are collected as safe backfills to apply before
/// stamping.</item>
/// <item>VersionTooNew: the history contains migration ids this application does not know.</item>
/// <item>InspectionFailed: any unknown or conflicting structure (extra or wrong-typed columns,
/// nullability mismatches, missing NOT NULL columns without a default, primary-key/foreign-key
/// mismatches, partial table sets, or a history that claims a version the schema contradicts).
/// Unknown state is never stamped, repaired, or silently accepted.</item>
/// </list>
/// <para>
/// <c>ExecuteAsync</c> re-verifies immediately before any write, applies the safe backfills,
/// stamps only the verified baseline in its own parameterized transaction (the stamp point is
/// after the structure verification, never at startup), and lets EF Core run the real migrations
/// on the empty state. Rejected states fail startup with the fixed error code
/// <see cref="IncompatibleErrorCode"/> and a structure-difference summary; the error surfaces and
/// logs never contain SQL statement text or connection values.
/// </para>
/// </remarks>
public sealed class DocthecaMigrationExecutor : IDatabaseMigrationExecutor
{
    internal const string InitialCreateMigrationId = "20260930161548_InitialCreate";
    internal const string HistoryTableName = "__EFMigrationsHistory";
    internal const string IncompatibleErrorCode = "DOCTHECA_DB_SCHEMA_INCOMPATIBLE";
    internal const string UpdatedAtTriggerName = "set_document_files_updated_at";

    /// <summary>The ordered migration contract this executor supports; frozen by the issue.</summary>
    internal static readonly IReadOnlyList<string> KnownMigrationIds = [InitialCreateMigrationId];

    /// <summary>The four business tables of the baseline.</summary>
    internal static readonly IReadOnlyList<string> KnownTableNames =
        ["document_files", "document_parses", "document_parse_images", "document_parse_blocks"];

    private const string SchemaName = "public";
    private const string InvalidCatalogSqlState = "3D000";

    private readonly DocthecaDbContext _context;
    private readonly ILogger? _logger;

    public DocthecaMigrationExecutor(DocthecaDbContext context, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _logger = logger;
    }

    /// <inheritdoc cref="IDatabaseMigrationExecutor.InspectAsync"/>
    /// <remarks>
    /// Explicit interface implementation for the shared orchestrator: the same read-only
    /// inspection as <see cref="InspectAsync"/>, translated into the ServiceMantle observation
    /// states (<see cref="DatabaseState.LegacyTakeoverRequired"/> becomes
    /// <see cref="MigrationObservationState.PendingMigration"/>).
    /// </remarks>
    async ValueTask<MigrationObservationState> IDatabaseMigrationExecutor.InspectAsync(
        CancellationToken cancellationToken)
    {
        var state = await InspectAsync(cancellationToken).ConfigureAwait(false);
        return ToObservationState(state);
    }

    /// <inheritdoc cref="IDatabaseMigrationExecutor.ExecuteAsync"/>
    /// <remarks>
    /// Explicit interface implementation: the identical execution path (re-verify, backfill,
    /// stamp, migrate, or refuse) observed by the orchestrator's cancellation token.
    /// </remarks>
    ValueTask IDatabaseMigrationExecutor.ExecuteAsync(CancellationToken cancellationToken) =>
        new(ExecuteAsync(cancellationToken));

    /// <summary>
    /// The fixed translation from this executor's classification to the ServiceMantle
    /// observation states consumed by <c>DatabaseMigrationOrchestrator</c>.
    /// </summary>
    internal static MigrationObservationState ToObservationState(DatabaseState state) => state switch
    {
        DatabaseState.Empty => MigrationObservationState.Empty,
        DatabaseState.CurrentVersionCompatible => MigrationObservationState.CurrentVersionCompatible,
        DatabaseState.LegacyTakeoverRequired => MigrationObservationState.PendingMigration,
        DatabaseState.VersionTooNew => MigrationObservationState.VersionTooNew,
        _ => MigrationObservationState.InspectionFailed,
    };

    /// <summary>The read-only classification of the target database.</summary>
    public enum DatabaseState
    {
        /// <summary>No business tables and no history: every migration really executes.</summary>
        Empty,

        /// <summary>The full known history is applied and the schema verifies.</summary>
        CurrentVersionCompatible,

        /// <summary>A verified legacy database without history: backfill, then stamp the baseline.</summary>
        LegacyTakeoverRequired,

        /// <summary>The history contains migration ids this application does not know.</summary>
        VersionTooNew,

        /// <summary>Any unknown or conflicting structure; execution is refused.</summary>
        InspectionFailed,
    }

    internal sealed record DatabaseInspection(
        DatabaseState State,
        string Reason,
        IReadOnlyList<BackfillStatement> Backfills,
        bool RequiresHistoryStamp)
    {
        internal static DatabaseInspection Of(
            DatabaseState state, string reason,
            IReadOnlyList<BackfillStatement>? backfills = null, bool requiresHistoryStamp = false) =>
            new(state, reason, backfills ?? [], requiresHistoryStamp);

        internal static DatabaseInspection Failed(string reason) =>
            new(DatabaseState.InspectionFailed, reason, [], false);
    }

    /// <summary>A safe, idempotent DDL statement collected during inspection.</summary>
    internal sealed record BackfillStatement(string Table, string Description, string Sql);

    public async Task<DatabaseState> InspectAsync(CancellationToken cancellationToken = default)
    {
        var inspection = await InspectDetailedAsync(cancellationToken).ConfigureAwait(false);
        return inspection.State;
    }

    internal async Task<DatabaseInspection> InspectDetailedAsync(CancellationToken cancellationToken)
    {
        EnsureMigrationContract();

        try
        {
            await _context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception)
            when (string.Equals(exception.SqlState, InvalidCatalogSqlState, StringComparison.Ordinal))
        {
            // The target database does not exist yet: exactly like the retired initializer, the
            // empty state lets EF Core Migrate create the database and apply every migration.
            return DatabaseInspection.Of(
                DatabaseState.Empty,
                "the target database does not exist yet");
        }
        catch (Exception exception) when (IsCancellation(exception, cancellationToken))
        {
            throw NewCancellation(exception, cancellationToken);
        }
        catch (Exception)
        {
            // Authentication, network, and permission failures are refused, never interpreted as
            // a missing database and never retried with creation fallbacks.
            _logger?.LogWarning(
                "Database inspection could not connect to the target database; refusing to classify it");
            return DatabaseInspection.Of(
                DatabaseState.InspectionFailed,
                "the target database connection could not be established");
        }

        try
        {
            return await InspectOpenDatabaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCancellation(exception, cancellationToken))
        {
            throw NewCancellation(exception, cancellationToken);
        }
        catch (Exception)
        {
            _logger?.LogWarning("Database inspection could not read the target structure");
            return DatabaseInspection.Failed(
                "the database structure could not be read");
        }
        finally
        {
            await _context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        EnsureMigrationContract();

        // Re-verify immediately before any write: the classification that got this call
        // scheduled may be stale, and duplicate runs must re-read the legitimate state instead
        // of double-stamping.
        var inspection = await InspectDetailedAsync(cancellationToken).ConfigureAwait(false);
        switch (inspection.State)
        {
            case DatabaseState.CurrentVersionCompatible:
                _logger?.LogInformation(
                    "Database is already compatible with the current version; nothing to execute");
                return;

            case DatabaseState.Empty:
                _logger?.LogInformation(
                    "Applying the {MigrationId} baseline migration to the empty database state ({Reason})",
                    InitialCreateMigrationId, inspection.Reason);
                cancellationToken.ThrowIfCancellationRequested();
                await _context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                _logger?.LogInformation("Database initialized from the migration baseline");
                return;

            case DatabaseState.LegacyTakeoverRequired:
                _logger?.LogInformation(
                    "Taking over a verified legacy database: {BackfillCount} safe structure backfill(s), then registering the {MigrationId} baseline",
                    inspection.Backfills.Count, InitialCreateMigrationId);
                cancellationToken.ThrowIfCancellationRequested();
                await ApplyBackfillsAsync(inspection.Backfills, cancellationToken).ConfigureAwait(false);
                await StampBaselineAsync(cancellationToken).ConfigureAwait(false);
                _logger?.LogInformation(
                    "Legacy database taken over: schema verified, data preserved, baseline registered");
                return;

            case DatabaseState.VersionTooNew:
            case DatabaseState.InspectionFailed:
            default:
                throw new InvalidOperationException(
                    $"[{IncompatibleErrorCode}] Database migration was requested but the target state is " +
                    $"{inspection.State}: {inspection.Reason}. Refusing to take over the database; no changes " +
                    "were made. Back up the database and reconcile its structure with the migration baseline " +
                    "manually before restarting.");
        }
    }

    // ---------- inspection ----------

    private async Task<DatabaseInspection> InspectOpenDatabaseAsync(CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection();
        var expected = BuildExpectedTables();

        var tableNames = await QueryTableNamesAsync(connection, cancellationToken).ConfigureAwait(false);
        var presentBusinessTables = KnownTableNames.Where(tableNames.Contains).ToList();
        var applied = tableNames.Contains(HistoryTableName)
            ? await QueryAppliedMigrationIdsAsync(connection, cancellationToken).ConfigureAwait(false)
            : [];

        var unknownIds = applied.Where(id => !KnownMigrationIds.Contains(id)).ToList();
        if (unknownIds.Count > 0)
        {
            return DatabaseInspection.Of(
                DatabaseState.VersionTooNew,
                $"the migration history contains ids this application does not know: {Join(unknownIds)}");
        }

        var shared = await new PostgreSqlSchemaEvidenceReader().ReadAsync((NpgsqlConnection)connection, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (shared.State == SchemaEvidenceReadState.TargetDatabaseMissing)
            return DatabaseInspection.Of(DatabaseState.Empty, "the target database does not exist yet");
        if (shared.State == SchemaEvidenceReadState.ReadFailed &&
            !await CanAdaptUnrepresentableModelAsync(connection, shared, cancellationToken).ConfigureAwait(false))
            return DatabaseInspection.Failed("the shared schema evidence could not be read");
        var snapshot = await ReadProviderSchemaEvidenceAsync(connection, shared, cancellationToken).ConfigureAwait(false);

        if (applied.Count == KnownMigrationIds.Count)
        {
            // The history claims the current version; the complete current schema must verify.
            var mismatch = ValidateTables(snapshot, expected, KnownTableNames, backfills: null);
            return mismatch is null
                ? DatabaseInspection.Of(
                    DatabaseState.CurrentVersionCompatible,
                    "the full known history is applied and the current schema verifies")
                : DatabaseInspection.Failed(
                    $"the history claims the current version but the schema does not verify: {mismatch.Reason}");
        }

        if (presentBusinessTables.Count == 0)
        {
            // No business tables at all (history absent): EF runs the baseline migration.
            return DatabaseInspection.Of(
                DatabaseState.Empty,
                "no business tables and no applied migrations are present");
        }

        if (presentBusinessTables.Count != KnownTableNames.Count)
        {
            // Partial table sets without a full history are refused.
            return DatabaseInspection.Failed(
                $"business tables are partially present: found [{Join(presentBusinessTables)}] but the baseline requires all of [{Join(KnownTableNames)}]");
        }

        // All four tables are present without history: verify and collect safe backfills.
        var backfills = new List<BackfillStatement>();
        var verification = ValidateTables(snapshot, expected, KnownTableNames, backfills);
        if (verification is not null)
        {
            return DatabaseInspection.Failed(verification.Reason);
        }

        // The updated_at trigger belongs to the baseline; legacy databases that predate it get
        // the same idempotent creation the retired initializer applied on every startup.
        backfills.Add(new BackfillStatement(
            "document_files",
            "updated_at trigger",
            """
            CREATE OR REPLACE FUNCTION set_document_files_updated_at()
            RETURNS TRIGGER AS $$
            BEGIN
                NEW.updated_at = NOW();
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            DROP TRIGGER IF EXISTS set_document_files_updated_at ON document_files;
            CREATE TRIGGER set_document_files_updated_at
                BEFORE UPDATE ON document_files
                FOR EACH ROW
                EXECUTE FUNCTION set_document_files_updated_at();
            """));

        return DatabaseInspection.Of(
            DatabaseState.LegacyTakeoverRequired,
            "a verified legacy database without migration history requires the safe backfills and the baseline registration",
            backfills,
            requiresHistoryStamp: true);
    }

    private void EnsureMigrationContract()
    {
        var assemblyMigrations = _context.Database.GetMigrations().ToList();
        if (!assemblyMigrations.SequenceEqual(KnownMigrationIds, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"[{IncompatibleErrorCode}] The migration assembly contains [{string.Join(", ", assemblyMigrations)}] " +
                $"but this executor only supports [{string.Join(", ", KnownMigrationIds)}]. Adding or changing " +
                "migrations requires extending the executor's known-version contract.");
        }
    }

    private static async Task<HashSet<string>> QueryTableNamesAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind = 'r'
            """;
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<List<string>> QueryAppliedMigrationIdsAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""SELECT "MigrationId" FROM public."{HistoryTableName}" ORDER BY "MigrationId" """;
        var ids = new List<string>();
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task<bool> CanAdaptUnrepresentableModelAsync(DbConnection connection,
        SchemaEvidenceReadResult result, CancellationToken cancellationToken)
    {
        // The reader reports no exception payload. Adapt only catalog-model construction
        // steps, and prove an unsupported shape with a scalar catalog observation. This is
        // not a second global snapshot: only a boolean leaves this diagnostic query; the
        // fallback facts themselves are restricted to the public four business tables.
        if (result.Message != "The schema evidence of 'pg_constraint' could not be read." &&
            result.Message != "The schema evidence of 'pg_attribute' could not be read.") return false;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH observed_tables AS (
                SELECT c.oid, c.relname, n.nspname FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relkind = 'r' AND n.nspname NOT IN ('pg_catalog', 'information_schema')
                  AND n.nspname !~ '^pg_toast' AND n.nspname !~ '^pg_temp_'
                  AND c.relname <> '__EFMigrationsHistory'
            )
            SELECT EXISTS (
                SELECT 1 FROM pg_catalog.pg_index i JOIN observed_tables t ON t.oid = i.indrelid
                WHERE NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint con WHERE con.conindid = i.indexrelid)
                  AND NOT EXISTS (SELECT 1 FROM unnest(i.indkey) k WHERE k = 0)
                GROUP BY i.indrelid, i.indkey, i.indisunique HAVING count(*) > 1
            ) OR EXISTS (
                SELECT 1 FROM pg_catalog.pg_constraint con JOIN observed_tables t ON t.oid = con.conrelid
                WHERE con.contype = 'f'
                GROUP BY con.conrelid, con.conkey, con.confrelid, con.confkey, con.confdeltype HAVING count(*) > 1
            ) OR EXISTS (
                SELECT 1 FROM observed_tables t WHERE
                    t.relname ~ '[[:cntrl:]]' OR btrim(t.relname) = '' OR
                    t.nspname ~ '[[:cntrl:]]' OR btrim(t.nspname) = '' OR
                    NOT EXISTS (SELECT 1 FROM pg_catalog.pg_attribute a WHERE a.attrelid = t.oid AND a.attnum > 0 AND NOT a.attisdropped)
            ) OR EXISTS (
                SELECT 1 FROM pg_catalog.pg_attribute a JOIN observed_tables t ON t.oid = a.attrelid
                WHERE a.attnum > 0 AND NOT a.attisdropped AND
                    (a.attname ~ '[[:cntrl:]]' OR btrim(a.attname) = '' OR
                     length(format_type(a.atttypid, a.atttypmod)) > 128 OR format_type(a.atttypid, a.atttypmod) ~ '[[:cntrl:]]')
            )
            """;
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task<ProviderSchemaEvidence> ReadProviderSchemaEvidenceAsync(
        DbConnection connection, SchemaEvidenceReadResult shared, CancellationToken cancellationToken)
    {
        var tables = KnownTableNames.ToArray();
        var snapshot = new ProviderSchemaEvidence();

        if (shared.State == SchemaEvidenceReadState.Succeeded)
        {
            // Ordinary column facts come exclusively from the shared reader. Other schemas
            // and tables are outside the original public four-table classification.
            foreach (var table in shared.Snapshot!.Tables.Where(table => table.Schema == SchemaName && KnownTableNames.Contains(table.Name)))
            foreach (var column in table.Columns)
                snapshot.AddColumn(table.Name, new ActualColumn(column.Name, column.DataType, !column.IsNullable));
        }
        else
        {
            // Only a proven neutral-model construction gap enters this bounded four-table
            // column read. Permission/network failures cannot masquerade as an empty snapshot.
            await using var columns = connection.CreateCommand();
            columns.CommandText = """
                SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN pg_attribute a ON a.attrelid = c.oid
                WHERE n.nspname = 'public' AND c.relkind = 'r' AND c.relname = ANY(@tables)
                  AND a.attnum > 0 AND NOT a.attisdropped
                ORDER BY c.relname, a.attnum
                """;
            AddTableArrayParameter(columns, tables);
            await using var reader = await columns
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                snapshot.AddColumn(
                    reader.GetString(0),
                    new ActualColumn(
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetBoolean(3)));
            }
        }

        await using (var constraints = connection.CreateCommand())
        {
            constraints.CommandText = """
                SELECT c.relname, con.conname, con.contype::text, con.confdeltype::text, fc.relname,
                       (SELECT array_agg(a.attname ORDER BY k.ord)
                          FROM unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord)
                          JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum),
                       (SELECT array_agg(a.attname ORDER BY k.ord)
                          FROM unnest(con.confkey) WITH ORDINALITY AS k(attnum, ord)
                          JOIN pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = k.attnum)
                FROM pg_constraint con
                JOIN pg_class c ON c.oid = con.conrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                LEFT JOIN pg_class fc ON fc.oid = con.confrelid
                WHERE n.nspname = 'public' AND c.relname = ANY(@tables)
                  AND con.contype = ANY(ARRAY['p', 'f']::"char"[])
                ORDER BY c.relname, con.conname
                """;
            AddTableArrayParameter(constraints, tables);
            await using var reader = await constraints
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var table = reader.GetString(0);
                var name = reader.GetString(1);
                var kind = reader.GetString(2);
                var deleteRule = reader.IsDBNull(3) ? null : reader.GetString(3);
                var principalTable = reader.IsDBNull(4) ? null : reader.GetString(4);
                var constraintColumns = ReadTextArray(reader, 5) ?? [];
                var principalColumns = ReadTextArray(reader, 6) ?? [];
                if (kind == "p")
                {
                    snapshot.SetPrimaryKey(table, new ActualConstraint(name, constraintColumns));
                }
                else
                {
                    snapshot.AddForeignKey(table, new ActualForeignKey(
                        name, constraintColumns, principalTable ?? string.Empty, principalColumns, deleteRule ?? "a"));
                }
            }
        }

        await using (var indexes = connection.CreateCommand())
        {
            indexes.CommandText = """
                SELECT c.relname, ic.relname, i.indisunique, array_length(i.indkey, 1),
                       (SELECT array_agg(a.attname ORDER BY k.ord)
                          FROM unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord)
                          JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum)
                FROM pg_index i
                JOIN pg_class ic ON ic.oid = i.indexrelid
                JOIN pg_class c ON c.oid = i.indrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relname = ANY(@tables)
                  AND NOT i.indisprimary
                  AND NOT EXISTS (
                      SELECT 1 FROM pg_constraint con WHERE con.conindid = i.indexrelid
                  )
                ORDER BY c.relname, ic.relname
                """;
            AddTableArrayParameter(indexes, tables);
            await using var reader = await indexes
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var keyCount = reader.IsDBNull(3) ? -1 : reader.GetInt32(3);
                var indexColumns = ReadTextArray(reader, 4) ?? [];
                snapshot.AddIndex(
                    reader.GetString(0),
                    new ActualIndex(
                        reader.GetString(1),
                        indexColumns,
                        reader.GetBoolean(2),
                        keyCount));
            }
        }

        return snapshot;
    }

    private static void AddTableArrayParameter(DbCommand command, string[] tables)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tables";
        parameter.Value = tables;
        command.Parameters.Add(parameter);
    }

    private static string[]? ReadTextArray(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<string[]>(ordinal);

    // ---------- shared expectation + product metadata ----------

    private IReadOnlyDictionary<string, ExpectedTable> BuildExpectedTables()
    {
        var shared = EfCoreExpectedSchemaDerivation.Derive(_context.Model);
        var relational = _context.Model.GetRelationalModel();
        var result = new Dictionary<string, ExpectedTable>(StringComparer.Ordinal);
        foreach (var table in shared.Tables)
        {
            if (!KnownTableNames.Contains(table.Name))
                throw new InvalidOperationException($"[{IncompatibleErrorCode}] The EF model contains table '{table.Name}' outside the supported migration contract.");
            var metadata = relational.Tables.Single(item => item.Name == table.Name);
            // Shared derivation supplies all neutral types/shapes. Metadata retained here is
            // limited to actual expected names and defaults needed to render safe backfill DDL.
            var columns = table.Columns.Select(column => new ExpectedColumn(column.Name, column.DataType,
                !column.IsNullable, metadata.Columns.Single(item => item.Name == column.Name).DefaultValue,
                metadata.Columns.Single(item => item.Name == column.Name).DefaultValueSql)).ToList();
            var primary = table.PrimaryKey is null ? null : new ExpectedConstraint(metadata.PrimaryKey!.Name, table.PrimaryKey.Columns);
            var foreign = metadata.ForeignKeyConstraints.Zip(table.ForeignKeys,
                (name, shape) => new ExpectedForeignKey(name.Name, shape)).ToList();
            var indexes = metadata.Indexes.Zip(table.Indexes,
                (name, shape) => new ExpectedIndex(name.Name, shape.Columns, shape.IsUnique)).ToList();
            result.Add(table.Name, new ExpectedTable(table.Name, columns, primary, foreign, indexes));
        }
        if (KnownTableNames.Any(name => !result.ContainsKey(name)))
            throw new InvalidOperationException($"[{IncompatibleErrorCode}] The EF model does not contain every contract table.");
        return result;
    }

    // ---------- unique difference -> refusal / safe backfill / ignore ----------

    private static VerificationFailure? ValidateTables(ProviderSchemaEvidence snapshot,
        IReadOnlyDictionary<string, ExpectedTable> expected, IEnumerable<string> tableNames,
        List<BackfillStatement>? backfills)
    {
        foreach (var tableName in tableNames)
        {
            var table = expected[tableName];
            var columns = snapshot.GetColumns(tableName).Values.Select(column =>
                new SchemaColumn(column.Name, column.Type, !column.NotNull)).ToList();
            var requiredColumns = table.Columns.Select(column => new SchemaColumn(column.Name, column.Type, !column.NotNull)).ToList();
            // No identity or default comparison was part of the original contract. Both sides
            // use None; defaults remain only product backfill evidence. Schema is public.
            var columnDifferences = SchemaEvidenceComparer.Compare(
                new NeutralSnapshot(columns.Count == 0 ? [] : [new SchemaTable(tableName, columns, schema: SchemaName)]),
                new ExpectedSchema([new SchemaTable(tableName, requiredColumns, schema: SchemaName)]));
            var failure = DecideDifferences(columnDifferences, table, backfills);
            if (failure is not null) return failure;

            var primary = snapshot.GetPrimaryKey(tableName);
            if (primary is not null && table.PrimaryKey is not null && !NameMatches(primary.Name, table.PrimaryKey.Name))
                return new VerificationFailure($"table '{tableName}' has an unexpected primary key name");
            failure = CompareShape(table, columns,
                expectedPrimary: table.PrimaryKey is null ? null : new SchemaPrimaryKey(table.PrimaryKey.Columns),
                actualPrimary: primary is null ? null : new SchemaPrimaryKey(primary.Columns));
            if (failure is not null) return failure;

            var foreign = snapshot.GetForeignKeys(tableName);
            foreach (var key in table.ForeignKeys)
            {
                var actual = foreign.FirstOrDefault(candidate => NameMatches(candidate.Name, key.Name));
                failure = CompareShape(table, columns, expectedForeign: key.Shape,
                    actualForeign: actual is null ? null : new SchemaForeignKey(actual.Columns, actual.PrincipalTable,
                        actual.PrincipalColumns, DeleteRule(actual.DeleteRule)));
                if (failure is not null) return failure;
            }
            if (foreign.Count != table.ForeignKeys.Count)
                return new VerificationFailure($"table '{tableName}' contains a foreign key outside the baseline");

            var indexes = snapshot.GetIndexes(tableName);
            foreach (var index in table.Indexes)
            {
                var actual = indexes.FirstOrDefault(candidate => NameMatches(candidate.Name, index.Name));
                // indkey includes INCLUDE attributes; expressions cannot become a plain column
                // list. These provider facts are absent from the neutral model.
                if (actual is not null && (actual.KeyCount != index.Columns.Count || actual.ResolvedColumns.Length == 0))
                    return new VerificationFailure($"index '{index.Name}' on table '{tableName}' does not match the baseline's column mapping or key count");
                var actualTable = new SchemaTable(tableName, columns, indexes: actual is null ? [] : [new SchemaIndex(actual.ResolvedColumns, actual.IsUnique)], schema: SchemaName);
                var expectedTable = new SchemaTable(tableName, columns, indexes: [new SchemaIndex(index.Columns, index.IsUnique)], schema: SchemaName);
                // Pair by actual required NAME before comparison. A global shape match would
                // incorrectly accept two required indexes whose names have swapped shapes.
                var differences = SchemaEvidenceComparer.Compare(new NeutralSnapshot([actualTable]), new ExpectedSchema([expectedTable]));
                failure = DecideDifferences(differences, table, backfills, index);
                if (failure is not null) return failure;
            }
        }
        return null;
    }

    private static VerificationFailure? CompareShape(ExpectedTable table, IReadOnlyList<SchemaColumn> observedColumns,
        SchemaPrimaryKey? expectedPrimary = null, SchemaPrimaryKey? actualPrimary = null,
        SchemaForeignKey? expectedForeign = null, SchemaForeignKey? actualForeign = null)
    {
        var name = table.Name;
        // Both envelopes carry the same OBSERVED columns; only this named object's shape is
        // compared, so index/FK matching cannot cross-associate unrelated required names.
        static SchemaForeignKey Normalize(SchemaForeignKey key) => new(key.Columns, key.ReferencedTable, key.ReferencedColumns, key.DeleteRule);
        var expected = new SchemaTable(name, observedColumns, expectedPrimary,
            expectedForeign is null ? [] : [Normalize(expectedForeign)], schema: SchemaName);
        var actual = new SchemaTable(name, observedColumns, actualPrimary,
            actualForeign is null ? [] : [Normalize(actualForeign)], schema: SchemaName);
        var differences = SchemaEvidenceComparer.Compare(new NeutralSnapshot([actual]), new ExpectedSchema([expected]));
        return DecideDifferences(differences, table, backfills: null);
    }

    private static VerificationFailure? DecideDifferences(IReadOnlyList<SchemaDifference> differences,
        ExpectedTable table, List<BackfillStatement>? backfills, ExpectedIndex? pairedIndex = null)
    {
        foreach (var difference in differences)
        {
            switch (difference.Kind)
            {
                case SchemaDifferenceKind.ExtraTable:
                case SchemaDifferenceKind.ColumnIdentityMismatch:
                    // Outside the original comparison dimensions (also normalized out above).
                    continue;
                case SchemaDifferenceKind.MissingColumn:
                    var column = table.Columns.Single(item => item.Name == difference.ColumnName);
                    if (backfills is null || !IsSafeBackfill(column))
                        return new VerificationFailure($"table '{table.Name}' is missing column '{column.Name}' and it cannot be backfilled in this state");
                    backfills.Add(new BackfillStatement(table.Name, $"column {column.Name}",
                        $"ALTER TABLE {table.Name} ADD COLUMN IF NOT EXISTS {column.Name} {BuildColumnDefinition(column)}"));
                    break;
                case SchemaDifferenceKind.IndexMismatch when pairedIndex is not null && difference.ActualIndex is null && difference.ExpectedIndex is not null && backfills is not null:
                    backfills.Add(new BackfillStatement(table.Name, $"index {pairedIndex.Name}",
                        $"CREATE {(pairedIndex.IsUnique ? "UNIQUE " : "")}INDEX IF NOT EXISTS {pairedIndex.Name} ON {table.Name} ({string.Join(", ", pairedIndex.Columns)})"));
                    break;
                case SchemaDifferenceKind.MissingTable:
                case SchemaDifferenceKind.ExtraColumn:
                case SchemaDifferenceKind.ColumnTypeMismatch:
                case SchemaDifferenceKind.ColumnNullabilityMismatch:
                case SchemaDifferenceKind.PrimaryKeyMismatch:
                case SchemaDifferenceKind.ForeignKeyMismatch:
                case SchemaDifferenceKind.IndexMismatch:
                default:
                    return new VerificationFailure($"table '{table.Name}' has a {difference.Kind}" +
                        (difference.ColumnName is null ? " against the baseline" : $" for column '{difference.ColumnName}'"));
            }
        }
        return null;
    }

    private static SchemaForeignKeyDeleteRule DeleteRule(string rule) => rule switch
    {
        "c" => SchemaForeignKeyDeleteRule.Cascade, "r" => SchemaForeignKeyDeleteRule.Restrict,
        "n" => SchemaForeignKeyDeleteRule.SetNull, "d" => SchemaForeignKeyDeleteRule.SetDefault,
        _ => SchemaForeignKeyDeleteRule.NoAction,
    };

    private static bool IsSafeBackfill(ExpectedColumn column) =>
        !column.NotNull || column.DefaultValue is not null || !string.IsNullOrEmpty(column.DefaultValueSql);

    private static string BuildColumnDefinition(ExpectedColumn column)
    {
        if (!column.NotNull)
        {
            return $"{column.Type} NULL";
        }

        if (!string.IsNullOrEmpty(column.DefaultValueSql))
        {
            return $"{column.Type} NOT NULL DEFAULT {column.DefaultValueSql}";
        }

        if (column.DefaultValue is not null)
        {
            return $"{column.Type} NOT NULL DEFAULT '{column.DefaultValue}'";
        }

        return $"{column.Type} NOT NULL";
    }

    /// <summary>
    /// Known constraint and index names are compared case-insensitively; the semantics
    /// (columns, uniqueness, references, delete behavior) must still match exactly.
    /// </summary>
    private static bool NameMatches(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private sealed record VerificationFailure(string Reason);

    // ---------- execution ----------

    private async Task ApplyBackfillsAsync(
        IReadOnlyList<BackfillStatement> backfills, CancellationToken cancellationToken)
    {
        foreach (var backfill in backfills)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _context.Database.ExecuteSqlRawAsync(backfill.Sql, cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation(
                "Applied legacy structure backfill on {Table}: {Description}",
                backfill.Table, backfill.Description);
        }
    }

    private async Task StampBaselineAsync(CancellationToken cancellationToken)
    {
        await _context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The shared writer owns the independent history transaction. No EF/ambient
            // transaction wraps it; the product still owns verification/backfill/stamp order.
            await new EfCoreMigrationBaselineWriter(_context).WriteBaselineAsync(
                _context.Database.GetDbConnection(), InitialCreateMigrationId, EfProductVersion,
                cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation(
                "Registered the verified {MigrationId} baseline for the legacy database",
                InitialCreateMigrationId);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static string EfProductVersion { get; } =
        typeof(DbContext).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? typeof(DbContext).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    // ---------- safe diagnostics ----------

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);

    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested || exception is OperationCanceledException;

    private static OperationCanceledException NewCancellation(
        Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException canceled
            ? canceled
            : new OperationCanceledException(
                "Database migration work was cancelled.", cancellationToken);
}

// ---------- shapes ----------

internal sealed record ExpectedColumn(
    string Name,
    string Type,
    bool NotNull,
    object? DefaultValue,
    string? DefaultValueSql);

internal sealed record ExpectedConstraint(string Name, IReadOnlyList<string> Columns);

internal sealed record ExpectedForeignKey(string Name, SchemaForeignKey Shape);

internal sealed record ExpectedIndex(string Name, IReadOnlyList<string> Columns, bool IsUnique);

internal sealed record ExpectedTable(
    string Name,
    IReadOnlyList<ExpectedColumn> Columns,
    ExpectedConstraint? PrimaryKey,
    IReadOnlyList<ExpectedForeignKey> ForeignKeys,
    IReadOnlyList<ExpectedIndex> Indexes);

internal sealed record ActualColumn(string Name, string Type, bool NotNull);

internal sealed record ActualConstraint(string Name, string[] Columns);

internal sealed record ActualForeignKey(
    string Name,
    string[] Columns,
    string PrincipalTable,
    string[] PrincipalColumns,
    string DeleteRule);

internal sealed record ActualIndex(
    string Name,
    string[] ResolvedColumns,
    bool IsUnique,
    int KeyCount);

/// <summary>
/// The actual structure read from PostgreSQL catalogs for the known business tables.
/// </summary>
internal sealed class ProviderSchemaEvidence
{
    private readonly Dictionary<string, Dictionary<string, ActualColumn>> _columns =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActualConstraint?> _primaryKeys =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ActualForeignKey>> _foreignKeys =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ActualIndex>> _indexes =
        new(StringComparer.Ordinal);

    internal void AddColumn(string table, ActualColumn column)
    {
        if (!_columns.TryGetValue(table, out var columns))
        {
            columns = new Dictionary<string, ActualColumn>(StringComparer.Ordinal);
            _columns[table] = columns;
        }

        columns[column.Name] = column;
    }

    internal void SetPrimaryKey(string table, ActualConstraint primaryKey)
    {
        _primaryKeys[table] = primaryKey;
    }

    internal void AddForeignKey(string table, ActualForeignKey foreignKey)
    {
        if (!_foreignKeys.TryGetValue(table, out var list))
        {
            list = [];
            _foreignKeys[table] = list;
        }

        list.Add(foreignKey);
    }

    internal void AddIndex(string table, ActualIndex index)
    {
        if (!_indexes.TryGetValue(table, out var list))
        {
            list = [];
            _indexes[table] = list;
        }

        list.Add(index);
    }

    internal IReadOnlyDictionary<string, ActualColumn> GetColumns(string table) =>
        _columns.TryGetValue(table, out var columns)
            ? columns
            : new Dictionary<string, ActualColumn>(StringComparer.Ordinal);

    internal ActualConstraint? GetPrimaryKey(string table) =>
        _primaryKeys.TryGetValue(table, out var primaryKey) ? primaryKey : null;

    internal IReadOnlyList<ActualForeignKey> GetForeignKeys(string table) =>
        _foreignKeys.TryGetValue(table, out var list) ? list : [];

    internal IReadOnlyList<ActualIndex> GetIndexes(string table) =>
        _indexes.TryGetValue(table, out var list) ? list : [];
}
