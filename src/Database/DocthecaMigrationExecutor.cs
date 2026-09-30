using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using ServiceMantle.Migration;

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
        catch (Exception exception)
        {
            // Authentication, network, and permission failures are refused, never interpreted as
            // a missing database and never retried with creation fallbacks.
            _logger?.LogWarning(exception,
                "Database inspection could not connect to the target database; refusing to classify it");
            return DatabaseInspection.Of(
                DatabaseState.InspectionFailed,
                "the target database connection could not be established (see service logs for the driver error)");
        }

        try
        {
            return await InspectOpenDatabaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCancellation(exception, cancellationToken))
        {
            throw NewCancellation(exception, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Database inspection could not read the target structure");
            return DatabaseInspection.Failed(
                "the database structure could not be read (see service logs for the driver error)");
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

        var snapshot = await ReadSchemaSnapshotAsync(connection, cancellationToken).ConfigureAwait(false);

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
            $"""SELECT "MigrationId" FROM "{HistoryTableName}" ORDER BY "MigrationId" """;
        var ids = new List<string>();
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task<SchemaSnapshot> ReadSchemaSnapshotAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        var tables = KnownTableNames.ToArray();
        var snapshot = new SchemaSnapshot();

        await using (var columns = connection.CreateCommand())
        {
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

    // ---------- expected schema (derived from the current EF model) ----------

    private IReadOnlyDictionary<string, ExpectedTable> BuildExpectedTables()
    {
        var expected = new Dictionary<string, ExpectedTable>(StringComparer.Ordinal);
        foreach (var entityType in _context.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            var storeObject = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table);
            if (tableName is null || storeObject is null)
            {
                continue;
            }

            if (!KnownTableNames.Contains(tableName))
            {
                throw new InvalidOperationException(
                    $"[{IncompatibleErrorCode}] The EF model contains table '{tableName}' outside the supported migration contract.");
            }

            var columns = entityType.GetProperties()
                .Select(property => new ExpectedColumn(
                    property.GetColumnName(storeObject.Value)!,
                    property.GetColumnType(storeObject.Value)!,
                    !property.IsNullable,
                    property.GetDefaultValue(),
                    property.GetDefaultValueSql()))
                .ToList();

            var primaryKey = entityType.FindPrimaryKey();
            ExpectedConstraint? expectedPrimaryKey = primaryKey is null
                ? null
                : new ExpectedConstraint(
                    primaryKey.GetName()!,
                    primaryKey.Properties
                        .Select(property => property.GetColumnName(storeObject.Value)!)
                        .ToList());

            var foreignKeys = entityType.GetForeignKeys()
                .Select(foreignKey =>
                {
                    var principalEntity = foreignKey.PrincipalEntityType;
                    var principalStoreObject =
                        StoreObjectIdentifier.Create(principalEntity, StoreObjectType.Table)!.Value;
                    return new ExpectedForeignKey(
                        foreignKey.GetConstraintName(storeObject.Value, principalStoreObject)!,
                        foreignKey.Properties
                            .Select(property => property.GetColumnName(storeObject.Value)!)
                            .ToList(),
                        principalEntity.GetTableName()!,
                        foreignKey.PrincipalKey.Properties
                            .Select(property => property.GetColumnName(principalStoreObject)!)
                            .ToList(),
                        MapDeleteRule(foreignKey.DeleteBehavior));
                })
                .ToList();

            var indexes = entityType.GetIndexes()
                .Select(index => new ExpectedIndex(
                    index.GetDatabaseName(storeObject.Value)!,
                    index.Properties
                        .Select(property => property.GetColumnName(storeObject.Value)!)
                        .ToList(),
                    index.IsUnique))
                .ToList();

            expected[tableName] = new ExpectedTable(
                tableName, columns, expectedPrimaryKey, foreignKeys, indexes);
        }

        foreach (var tableName in KnownTableNames.Where(name => !expected.ContainsKey(name)))
        {
            throw new InvalidOperationException(
                $"[{IncompatibleErrorCode}] The EF model does not contain the contract table '{tableName}'.");
        }

        return expected;
    }

    private static string MapDeleteRule(DeleteBehavior behavior) => behavior switch
    {
        DeleteBehavior.Cascade or DeleteBehavior.ClientCascade => "c",
        DeleteBehavior.Restrict => "r",
        DeleteBehavior.SetNull => "n",
        // Optional relationships default to ClientSetNull; migrations emit no ON DELETE clause
        // for them, which PostgreSQL stores as NO ACTION.
        DeleteBehavior.NoAction or DeleteBehavior.ClientNoAction or DeleteBehavior.ClientSetNull => "a",
        _ => throw new InvalidOperationException($"Unsupported delete behavior '{behavior}'."),
    };

    // ---------- validation ----------

    private static VerificationFailure? ValidateTables(
        SchemaSnapshot snapshot,
        IReadOnlyDictionary<string, ExpectedTable> expected,
        IEnumerable<string> tableNames,
        List<BackfillStatement>? backfills)
    {
        foreach (var tableName in tableNames)
        {
            var mismatch = ValidateTable(snapshot, expected[tableName], backfills);
            if (mismatch is not null)
            {
                return mismatch;
            }
        }

        return null;
    }

    private static VerificationFailure? ValidateTable(
        SchemaSnapshot snapshot, ExpectedTable expected, List<BackfillStatement>? backfills)
    {
        if (!snapshot.TableExists(expected.Name))
        {
            return new VerificationFailure($"table '{expected.Name}' is missing");
        }

        var actualColumns = snapshot.GetColumns(expected.Name);
        foreach (var column in expected.Columns)
        {
            if (!actualColumns.TryGetValue(column.Name, out var actual))
            {
                // A missing column is only safe to backfill when it is nullable or carries a
                // store default; a NOT NULL column without a default cannot be added without
                // inventing data, so the database is refused.
                if (backfills is null)
                {
                    return new VerificationFailure(
                        $"table '{expected.Name}' is missing column '{column.Name}'");
                }

                if (!IsSafeBackfill(column))
                {
                    return new VerificationFailure(
                        $"table '{expected.Name}' is missing column '{column.Name}' and it cannot be backfilled safely " +
                        "(it is NOT NULL without a store default)");
                }

                backfills.Add(new BackfillStatement(
                    expected.Name,
                    $"column {column.Name}",
                    $"ALTER TABLE {expected.Name} ADD COLUMN IF NOT EXISTS {column.Name} {BuildColumnDefinition(column)}"));
                continue;
            }

            if (!string.Equals(actual.Type, column.Type, StringComparison.Ordinal))
            {
                return new VerificationFailure(
                    $"column '{expected.Name}.{column.Name}' has type '{actual.Type}' but the baseline requires '{column.Type}'");
            }

            if (actual.NotNull != column.NotNull)
            {
                return new VerificationFailure(
                    $"column '{expected.Name}.{column.Name}' has nullability {(actual.NotNull ? "NOT NULL" : "NULL")} " +
                    $"but the baseline requires {(column.NotNull ? "NOT NULL" : "NULL")}");
            }
        }

        var extraColumn = actualColumns.Keys.FirstOrDefault(name =>
            expected.Columns.All(column => !string.Equals(column.Name, name, StringComparison.Ordinal)));
        if (extraColumn is not null)
        {
            return new VerificationFailure(
                $"table '{expected.Name}' contains column '{extraColumn}' outside the baseline");
        }

        if (expected.PrimaryKey is not null)
        {
            var actualPrimaryKey = snapshot.GetPrimaryKey(expected.Name);
            if (actualPrimaryKey is null)
            {
                return new VerificationFailure($"table '{expected.Name}' is missing its primary key");
            }

            if (!NameMatches(actualPrimaryKey.Name, expected.PrimaryKey.Name))
            {
                return new VerificationFailure(
                    $"table '{expected.Name}' has primary key '{actualPrimaryKey.Name}' but the baseline requires '{expected.PrimaryKey.Name}'");
            }

            if (!actualPrimaryKey.Columns.SequenceEqual(expected.PrimaryKey.Columns, StringComparer.Ordinal))
            {
                return new VerificationFailure(
                    $"primary key '{expected.PrimaryKey.Name}' covers [{string.Join(", ", actualPrimaryKey.Columns)}] " +
                    $"but the baseline requires [{string.Join(", ", expected.PrimaryKey.Columns)}]");
            }
        }

        var actualForeignKeys = snapshot.GetForeignKeys(expected.Name);
        foreach (var foreignKey in expected.ForeignKeys)
        {
            var actual = actualForeignKeys.FirstOrDefault(candidate =>
                NameMatches(candidate.Name, foreignKey.Name));
            if (actual is null)
            {
                return new VerificationFailure(
                    $"table '{expected.Name}' is missing foreign key '{foreignKey.Name}'");
            }

            if (!actual.Columns.SequenceEqual(foreignKey.Columns, StringComparer.Ordinal) ||
                !string.Equals(actual.PrincipalTable, foreignKey.PrincipalTable, StringComparison.Ordinal) ||
                !actual.PrincipalColumns.SequenceEqual(foreignKey.PrincipalColumns, StringComparer.Ordinal) ||
                !string.Equals(actual.DeleteRule, foreignKey.DeleteRule, StringComparison.Ordinal))
            {
                return new VerificationFailure(
                    $"foreign key '{foreignKey.Name}' on table '{expected.Name}' does not match the baseline's columns, reference, or delete behavior");
            }
        }

        if (actualForeignKeys.Count != expected.ForeignKeys.Count)
        {
            return new VerificationFailure(
                $"table '{expected.Name}' contains a foreign key outside the baseline");
        }

        var actualIndexes = snapshot.GetIndexes(expected.Name);
        foreach (var index in expected.Indexes)
        {
            var actual = actualIndexes.FirstOrDefault(candidate => NameMatches(candidate.Name, index.Name));
            if (actual is null)
            {
                if (backfills is null)
                {
                    return new VerificationFailure($"table '{expected.Name}' is missing index '{index.Name}'");
                }

                // A missing index is safe to backfill: CREATE INDEX IF NOT EXISTS is idempotent
                // and changes no data.
                backfills.Add(new BackfillStatement(
                    expected.Name,
                    $"index {index.Name}",
                    $"CREATE {(index.IsUnique ? "UNIQUE " : "")}INDEX IF NOT EXISTS {index.Name} ON {expected.Name} ({string.Join(", ", index.Columns)})"));
                continue;
            }

            if (actual.IsUnique != index.IsUnique ||
                actual.ResolvedColumns.Length != index.Columns.Count ||
                actual.KeyCount != index.Columns.Count ||
                !actual.ResolvedColumns.SequenceEqual(index.Columns, StringComparer.Ordinal))
            {
                return new VerificationFailure(
                    $"index '{index.Name}' on table '{expected.Name}' does not match the baseline's columns or uniqueness");
            }
        }

        return null;
    }

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
            await using var transaction = await _context.Database
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var connection = _context.Database.GetDbConnection();
            var dbTransaction = transaction.GetDbTransaction();

            await using (var createHistory = connection.CreateCommand())
            {
                createHistory.Transaction = dbTransaction;
                createHistory.CommandText = $$"""
                    CREATE TABLE IF NOT EXISTS "{{HistoryTableName}}" (
                        "MigrationId" character varying(150) NOT NULL,
                        "ProductVersion" character varying(32) NOT NULL,
                        CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
                    )
                    """;
                await createHistory.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var insertBaseline = connection.CreateCommand())
            {
                insertBaseline.Transaction = dbTransaction;
                // Duplicate registrations re-read the legitimate state instead of failing or
                // double-stamping; parameters keep the write injection-free.
                insertBaseline.CommandText = $$"""
                    INSERT INTO "{{HistoryTableName}}" ("MigrationId", "ProductVersion")
                    SELECT @migrationId, @productVersion
                    WHERE NOT EXISTS (
                        SELECT 1 FROM "{{HistoryTableName}}" WHERE "MigrationId" = @migrationId
                    )
                    """;
                var idParameter = insertBaseline.CreateParameter();
                idParameter.ParameterName = "@migrationId";
                idParameter.Value = InitialCreateMigrationId;
                insertBaseline.Parameters.Add(idParameter);
                var versionParameter = insertBaseline.CreateParameter();
                versionParameter.ParameterName = "@productVersion";
                versionParameter.Value = EfProductVersion;
                insertBaseline.Parameters.Add(versionParameter);
                await insertBaseline.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

internal sealed record ExpectedForeignKey(
    string Name,
    IReadOnlyList<string> Columns,
    string PrincipalTable,
    IReadOnlyList<string> PrincipalColumns,
    string DeleteRule);

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
internal sealed class SchemaSnapshot
{
    private readonly Dictionary<string, Dictionary<string, ActualColumn>> _columns =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActualConstraint?> _primaryKeys =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ActualForeignKey>> _foreignKeys =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ActualIndex>> _indexes =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _tables = new(StringComparer.Ordinal);

    internal void AddColumn(string table, ActualColumn column)
    {
        _tables.Add(table);
        if (!_columns.TryGetValue(table, out var columns))
        {
            columns = new Dictionary<string, ActualColumn>(StringComparer.Ordinal);
            _columns[table] = columns;
        }

        columns[column.Name] = column;
    }

    internal void SetPrimaryKey(string table, ActualConstraint primaryKey)
    {
        _tables.Add(table);
        _primaryKeys[table] = primaryKey;
    }

    internal void AddForeignKey(string table, ActualForeignKey foreignKey)
    {
        _tables.Add(table);
        if (!_foreignKeys.TryGetValue(table, out var list))
        {
            list = [];
            _foreignKeys[table] = list;
        }

        list.Add(foreignKey);
    }

    internal void AddIndex(string table, ActualIndex index)
    {
        _tables.Add(table);
        if (!_indexes.TryGetValue(table, out var list))
        {
            list = [];
            _indexes[table] = list;
        }

        list.Add(index);
    }

    internal bool TableExists(string table) => _tables.Contains(table);

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
