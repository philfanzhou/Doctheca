using System.Data;
using Doctheca.Database;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ServiceMantle;
using ServiceMantle.Web;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.PostgreSql;
using ServiceMantle.Database.PostgreSql.Migration;
using ServiceMantle.Health;
using ServiceMantle.Migration;
using ServiceMantle.Persistence.Relational;

namespace Doctheca.Host;

/// <summary>Consumer configuration and registration for the shared startup gate.</summary>
public static class DocthecaStartupDatabase
{
    internal const string AllowCreateConfigurationKey = "Database:AllowCreate";

    /// <summary>Registers the direct gate and the scoped health source, without a hosted run.</summary>
    public static IServiceCollection AddDocthecaStartupDatabase(
        this IServiceCollection services, ServiceMantleBuilder foundation)
    {
        foundation
            .AddBootstrapDatabaseProvider<PostgreSqlBootstrapDatabaseProvider>()
            .AddDatabaseTargetPreparationProvider<PostgreSqlDatabaseTargetPreparationProvider>()
            .AddMigrationLockProvider<PostgreSqlMigrationLockProvider>()
            .AddDatabaseMigration<DocthecaMigrationExecutor>();
        services.AddSingleton<IDatabaseDeploymentCapabilityProvider, DocthecaPostgreSqlDeploymentCapability>();
        services.AddSingleton<DatabaseDeploymentCapabilityRegistry>(sp => new(
            sp.GetServices<IDatabaseDeploymentCapabilityProvider>(),
            sp.GetRequiredService<BootstrapDatabaseProviderRegistry>().ProviderIdResolver));
        services.AddSingleton<StartupDatabaseReceipt>();
        services.AddSingleton<StartupDatabaseGate>();
        services.AddScoped<IServiceDatabaseProbeFailureClassifier, DocthecaPostgreSqlProbeFailureClassifier>();
        services.AddScoped<IServiceHealthSnapshotSource>(sp => new EfCoreHealthSnapshotSource<DocthecaDbContext>(
            sp.GetRequiredService<StartupDatabaseReceipt>(),
            sp.GetRequiredService<DocthecaDbContext>(),
            sp.GetRequiredService<IServiceDatabaseProbeFailureClassifier>(),
            DocthecaServiceMantleExtensions.ServiceIdValue,
            EfCoreHealthSnapshotProbeMode.MappedSchema));
        return services;
    }

    /// <summary>Reads the existing creation switch before any database I/O.</summary>
    public static StartupDatabaseGateOptions CreateOptions(IConfiguration configuration, string connectionString)
    {
        var raw = configuration[AllowCreateConfigurationKey];
        var allowCreate = false;
        if (!string.IsNullOrWhiteSpace(raw) && !bool.TryParse(raw, out allowCreate))
        {
            throw new InvalidOperationException(
                "Database:AllowCreate must be 'true' or 'false' (error database_target_preparation.invalid_target); refusing to start.");
        }
        return new StartupDatabaseGateOptions(
            new BootstrapDatabaseConfiguration(WellKnownDatabaseProviderIds.PostgreSql, null, connectionString),
            DatabaseDeploymentMode.MultiInstance,
            TimeSpan.FromSeconds(30),
            enableTargetPreparation: true,
            allowTargetCreation: allowCreate,
            maintenanceConnectionString: PostgreSqlMaintenanceConnection.DeriveConnectionString(connectionString),
            preparationTimeout: TimeSpan.FromSeconds(30));
    }
}

/// <summary>Declares PostgreSQL deployment support; the library owns validation and locking.</summary>
internal sealed class DocthecaPostgreSqlDeploymentCapability : IDatabaseDeploymentCapabilityProvider
{
    public DatabaseDeploymentCapability Capability { get; } = new(
        WellKnownDatabaseProviderIds.PostgreSql, DatabaseDeploymentSupport.SingleAndMultiInstance);

    public ValueTask<string> GetCanonicalTargetIdentityAsync(
        BootstrapDatabaseConfiguration target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = new NpgsqlConnectionStringBuilder(target.ConnectionString);
        var host = (connection.Host ?? string.Empty).ToLowerInvariant();
        var database = connection.Database ?? string.Empty;
        // Length delimiters avoid collisions; neither credentials nor usernames are included.
        return ValueTask.FromResult($"{host.Length}:{host}{connection.Port}:{database.Length}:{database}");
    }
}

/// <summary>Preserves the existing classification of all connection-opening failures.</summary>
internal sealed class DocthecaPostgreSqlProbeFailureClassifier(DocthecaDbContext context)
    : IServiceDatabaseProbeFailureClassifier
{
    private readonly PostgreSqlDatabaseProbeFailureClassifier provider = new();

    public ServiceDatabaseProbeFailureKind Classify(Exception exception) =>
        context.Database.GetDbConnection().State != ConnectionState.Open
            ? ServiceDatabaseProbeFailureKind.ConnectionFailure
            : provider.Classify(exception);
}
