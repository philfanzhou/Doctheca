using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using Doctheca.Database;

namespace Doctheca.Host.Health;

/// <summary>
/// The consumer-owned scoped readiness snapshot source for the ServiceMantle health endpoints.
/// </summary>
/// <remarks>
/// Read-only by contract: the probe never runs DDL, never parses/indexes, never triggers
/// installation or registration, and never reads business rows. It combines two facts:
/// <list type="number">
/// <item>The process-local <see cref="DocthecaStartupReceipt"/>: before the initializer
/// completed (or when it failed and the host somehow kept serving), migrationStatus can never
/// be published as Succeeded and readiness fails closed.</item>
/// <item>A cancellation-aware zero-row probe of the exact tables/columns the current EF model
/// maps: first the connection is opened, then each mapped table is queried with
/// <c>SELECT &lt;mapped columns&gt; FROM &lt;table&gt; WHERE FALSE</c>. Table and column names
/// come exclusively from the compiled EF model — never from request input — and no row data is
/// read or returned.</item>
/// </list>
/// Failure mapping (all value-free, fixed safe codes, no raw input or exception content):
/// connection failure → <c>(Completed, Succeeded, Unreachable, doctheca.database_unreachable)</c>;
/// mapped table/column probe failure → <c>(Completed, Failed, Reachable, doctheca.schema_unavailable)</c>;
/// unknown exceptions propagate so the library answers its own safe code; cancellation
/// propagates on the caller's own token. Every readiness request re-samples: nothing is cached,
/// so a recovered database is observed by the next request.
/// </remarks>
public sealed class DocthecaHealthSnapshotSource(
    DocthecaStartupReceipt startupReceipt,
    DocthecaDbContext dbContext) : IServiceHealthSnapshotSource
{
    /// <summary>The initializer has not completed in this process.</summary>
    public const string StartupIncompleteErrorCode = "doctheca.startup_incomplete";

    /// <summary>The current database connection could not be established.</summary>
    public const string DatabaseUnreachableErrorCode = "doctheca.database_unreachable";

    /// <summary>A mapped table or column could not be read.</summary>
    public const string SchemaUnavailableErrorCode = "doctheca.schema_unavailable";

    public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!startupReceipt.InitializationCompleted)
        {
            return new ServiceHealthSnapshot(
                ServiceStartupPhase.PendingSetup,
                ServiceMigrationReadinessState.Running,
                ServiceDatabaseReadinessState.Unreachable,
                StartupIncompleteErrorCode);
        }

        var connection = dbContext.Database.GetDbConnection();

        // Opening the connection is a pure reachability fact: whatever the failure type
        // (refused, timed out, broken pooled connector, transient driver state), the database
        // is not reachable for this probe. Cancellation still propagates on the caller's own
        // token. The receipt already proved the initializer completed in this process, so the
        // migration state stays Succeeded while the database is Unreachable; the next request
        // re-samples with a fresh scoped context.
        try
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new ServiceHealthSnapshot(
                ServiceStartupPhase.Completed,
                ServiceMigrationReadinessState.Succeeded,
                ServiceDatabaseReadinessState.Unreachable,
                DatabaseUnreachableErrorCode);
        }

        // The connection is open, so a failure here distinguishes a schema regression from a
        // connection that dropped mid-probe. Anything unknown propagates and the library
        // answers its own safe code (fail closed) instead of this source inventing a state.
        try
        {
            foreach (var probe in BuildMappedSchemaProbes())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = probe;
                await using var reader =
                    await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PostgresException postgresException) when (
            postgresException.SqlState is not null &&
            postgresException.SqlState.StartsWith("42", StringComparison.Ordinal))
        {
            // SQLSTATE class 42 (undefined object / access-rule violation): a mapped table or
            // column is not readable — dropped, renamed, or a permission regression. Fail
            // closed instead of trusting the legacy initializer's swallowed ALTER failures.
            return new ServiceHealthSnapshot(
                ServiceStartupPhase.Completed,
                ServiceMigrationReadinessState.Failed,
                ServiceDatabaseReadinessState.Reachable,
                SchemaUnavailableErrorCode);
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            // The connection dropped mid-probe (server went away): a reachability fact, not a
            // schema regression.
            return new ServiceHealthSnapshot(
                ServiceStartupPhase.Completed,
                ServiceMigrationReadinessState.Succeeded,
                ServiceDatabaseReadinessState.Unreachable,
                DatabaseUnreachableErrorCode);
        }

        return new ServiceHealthSnapshot(
            ServiceStartupPhase.Completed,
            ServiceMigrationReadinessState.Succeeded,
            ServiceDatabaseReadinessState.Reachable);
    }

    /// <summary>
    /// Classifies transport/lifecycle failures (network errors, server shutdown, resource
    /// exhaustion) as reachability facts. Used only after the connection was opened, to tell a
    /// mid-probe drop apart from a schema regression; unknown failures propagate so the library
    /// answers its own safe code.
    /// </summary>
    private static bool IsConnectionFailure(Exception exception) => exception switch
    {
        // A PostgresException without a SQLSTATE did not come from a parsed server error
        // response (it is transport-level), so it counts as a reachability fact.
        PostgresException postgres =>
            postgres.SqlState is null ||
            postgres.SqlState.StartsWith("08", StringComparison.Ordinal) || // connection exception
            postgres.SqlState.StartsWith("57", StringComparison.Ordinal) || // operator intervention (shutdown)
            postgres.SqlState.StartsWith("53", StringComparison.Ordinal) || // insufficient resources
            postgres.SqlState is "3D000" or "55P03", // invalid catalog name / lock not available
        NpgsqlException or TimeoutException => true,
        _ => false
    };

    /// <summary>
    /// Builds one zero-row SELECT per EF-mapped table, listing exactly the columns the current
    /// compiled model maps. The EF model is the only accepted source of table/column names.
    /// </summary>
    private IEnumerable<string> BuildMappedSchemaProbes()
    {
        foreach (var entityType in dbContext.Model.GetEntityTypes())
        {
            if (StoreObjectIdentifier.Create(entityType, StoreObjectType.Table) is not { } table)
            {
                continue;
            }

            var schema = Quote(table.Schema ?? "public");
            var columns = entityType.GetProperties()
                .Select(property => property.GetColumnName(table))
                .Where(column => !string.IsNullOrEmpty(column))
                .Distinct(StringComparer.Ordinal)
                .Select(column => Quote(column!));

            yield return
                $"SELECT {string.Join(", ", columns)} FROM {schema}.{Quote(table.Name)} WHERE FALSE";
        }
    }

    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
