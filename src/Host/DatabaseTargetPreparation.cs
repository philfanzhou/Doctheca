using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.PostgreSql;

namespace Doctheca.Host;

/// <summary>
/// A safe startup failure of the database deployment-validation / target-preparation stage.
/// Messages and error codes never contain connection strings, credentials, or other secrets.
/// </summary>
public sealed class DocthecaDatabaseTargetPreparationException : Exception
{
    /// <summary>Creates the exception with a stable, secret-free error code.</summary>
    /// <param name="errorCode">The safe error code describing the refusal.</param>
    /// <param name="message">The human-readable, secret-free refusal reason.</param>
    public DocthecaDatabaseTargetPreparationException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Gets the stable, secret-free error code describing the refusal.</summary>
    public string ErrorCode { get; }
}

/// <summary>
/// Validates the PostgreSQL target deployment and creates the target database when — and only
/// when — it is verifiably missing and explicitly allowed, before the migration orchestration
/// runs (issue #53).
/// </summary>
/// <remarks>
/// Contract (fixed by issue #53; the first two stages of the startup sequence):
/// <list type="bullet">
/// <item>Deployment validation: the shared provider observes the resolved connection string.
/// A connectable existing target is used as-is: no maintenance-database connection, no
/// <c>CREATE DATABASE</c>, and no CREATEDB or postgres-database privilege is required.</item>
/// <item>A verifiably missing target is created only when <c>Database:AllowCreate</c>
/// (environment form <c>Database__AllowCreate</c>, values <c>true</c>/<c>false</c> only,
/// default <c>false</c>) permits it. The implicit database creation that EF Core
/// <c>Migrate</c> used to perform is retired: by default a missing database refuses startup
/// with the fixed error code <see cref="CreationNotAllowedErrorCode"/> and writes nothing —
/// neither the catalog nor any table.</item>
/// <item>Creation uses the shared provider with a fixed 30-second budget. The maintenance
/// connection is a copy of the target connection string with only the database changed to
/// <c>postgres</c>; no additional or persisted administrative secret exists and the value
/// never reaches logs. After creation the target must observe as connectable again before
/// the migration orchestration may start.</item>
/// <item>Unreachable servers, authentication failures, permission failures, and server or
/// database identity conflicts are refused with the provider's safe error codes
/// (<c>database_target_preparation.*</c>); they are never reinterpreted as a missing database
/// and never answered with a creation fallback. An existing database is never dropped,
/// overwritten, or re-owned.</item>
/// </list>
/// </remarks>
public static class DocthecaDatabaseTargetPreparer
{
    /// <summary>
    /// The configuration key (environment form <c>Database__AllowCreate</c>) that enables
    /// creation of a verifiably missing target database. Defaults to <c>false</c>: creation is
    /// an explicit deployment decision, never an implicit startup side effect.
    /// </summary>
    public const string AllowCreateConfigurationKey = "Database:AllowCreate";

    /// <summary>
    /// The fixed refusal code when the target database is verifiably missing and creation is
    /// not allowed. Styled after the executor's <c>DOCTHECA_DB_SCHEMA_INCOMPATIBLE</c>.
    /// </summary>
    public const string CreationNotAllowedErrorCode = "DOCTHECA_DB_CREATION_NOT_ALLOWED";

    /// <summary>
    /// The fixed refusal code when <c>Database:AllowCreate</c> carries a value other than
    /// <c>true</c>/<c>false</c>. The invalid value itself is not echoed.
    /// </summary>
    public const string InvalidConfigurationErrorCode = "DOCTHECA_DB_ALLOW_CREATE_INVALID";

    /// <summary>The fixed preparation budget. There is deliberately no configuration key.</summary>
    internal static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs the deployment validation and target preparation with the real shared provider.
    /// </summary>
    /// <param name="configuration">The effective host configuration.</param>
    /// <param name="connectionString">The resolved target connection string.</param>
    /// <param name="logger">The startup logger; no connection value is ever logged.</param>
    /// <param name="cancellationToken">Observed throughout; cancellation stops startup before any migration work.</param>
    /// <exception cref="DocthecaDatabaseTargetPreparationException">The target must not or could not be prepared.</exception>
    /// <exception cref="OperationCanceledException">Preparation was cancelled.</exception>
    public static Task PrepareAsync(
        IConfiguration configuration,
        string connectionString,
        ILogger logger,
        CancellationToken cancellationToken = default) =>
        PrepareAsync(
            new PostgreSqlDatabaseTargetPreparationProvider(),
            configuration,
            connectionString,
            logger,
            cancellationToken);

    /// <summary>Test seam: runs the same preparation flow against an injected provider.</summary>
    internal static async Task PrepareAsync(
        IDatabaseTargetPreparationProvider provider,
        IConfiguration configuration,
        string connectionString,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var allowCreate = ReadAllowCreate(configuration);
        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            serverVersion: null,
            connectionString);

        var observation = await provider.ObserveAsync(target, cancellationToken).ConfigureAwait(false);
        switch (observation.Status)
        {
            case DatabaseTargetObservationStatus.TargetConnectable:
                // The existing target is used as-is: no maintenance connection and no CREATE.
                logger.LogInformation(
                    "Database target is connectable; using it as-is (existing databases are never modified)");
                return;

            case DatabaseTargetObservationStatus.TargetMissing:
                if (!allowCreate)
                {
                    throw new DocthecaDatabaseTargetPreparationException(
                        CreationNotAllowedErrorCode,
                        $"The target database is missing and {AllowCreateConfigurationKey} is not enabled; " +
                        $"refusing to start with {CreationNotAllowedErrorCode}. Create the database manually " +
                        $"or set {AllowCreateConfigurationKey}=true to let startup create it.");
                }

                await CreateMissingTargetAsync(provider, target, connectionString, logger, cancellationToken)
                    .ConfigureAwait(false);
                return;

            default:
                // ServerUnreachable and TargetUnreachable (authentication, permission, or identity
                // conflicts) are refusals, never a missing-database signal with a creation fallback.
                throw new DocthecaDatabaseTargetPreparationException(
                    observation.ErrorCode ?? WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed,
                    $"The database target could not be validated (status {observation.Status}" +
                    (observation.ErrorCode is null ? string.Empty : $", error {observation.ErrorCode}") +
                    "); refusing to start without a creation fallback.");
        }
    }

    private static async Task CreateMissingTargetAsync(
        IDatabaseTargetPreparationProvider provider,
        BootstrapDatabaseConfiguration target,
        string connectionString,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // The maintenance connection is a copy of the original connection string with only the
        // database changed to "postgres": the same user, host, port, and credentials. No
        // additional administrative secret is introduced, and the value stays in memory.
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
        };
        var request = new DatabaseTargetPreparationRequest(target, maintenanceBuilder.ConnectionString);

        var result = await provider
            .PrepareAsync(request, PreparationTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new DocthecaDatabaseTargetPreparationException(
                result.ErrorCode ?? WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed,
                $"Database target preparation failed (error {result.ErrorCode}); refusing to start.");
        }

        logger.LogInformation(
            "Database target preparation succeeded with outcome {Outcome}", result.Outcome);

        // Preparation alone is not trusted: the target must be connectable again with its own
        // identity before the migration orchestration starts.
        var reobservation = await provider.ObserveAsync(target, cancellationToken).ConfigureAwait(false);
        if (!reobservation.IsTargetConnectable)
        {
            throw new DocthecaDatabaseTargetPreparationException(
                reobservation.ErrorCode ?? WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed,
                $"The prepared database target is still not connectable (status {reobservation.Status}" +
                (reobservation.ErrorCode is null ? string.Empty : $", error {reobservation.ErrorCode}") +
                "); refusing to start the migration orchestration.");
        }
    }

    /// <summary>
    /// Reads the creation switch. A missing value keeps the safe default (<c>false</c>); an
    /// unparsable value fails startup instead of guessing, and the value is never echoed.
    /// </summary>
    internal static bool ReadAllowCreate(IConfiguration configuration)
    {
        var raw = configuration[AllowCreateConfigurationKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (bool.TryParse(raw, out var allowCreate))
        {
            return allowCreate;
        }

        throw new DocthecaDatabaseTargetPreparationException(
            InvalidConfigurationErrorCode,
            $"{AllowCreateConfigurationKey} must be 'true' or 'false'; refusing to start " +
            $"with {InvalidConfigurationErrorCode}.");
    }
}
