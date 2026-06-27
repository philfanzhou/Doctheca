using System.Data.Common;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ruoyu.Study.DocLibrary.Service;

// MIGRATION NOTICE (2026-06-27):
// One-time migration: renames PostgreSQL database from ruoyu_study_docretrieval to ruoyu_study_doclibrary.
//
// Phase 1 (old DB name in config): creates new DB as a copy of old DB using CREATE DATABASE ... TEMPLATE ...
//   - Requires no active connections to the old DB; we terminate them first.
//   - Safe because migration runs during startup before any background workers start.
//
// Phase 2 (new DB name in config): drops the old DB.
//   - After user updates start.sh / appsettings.json to use the new DB name.
//
// Safe to delete after 2026-07-07.
public static class DatabaseNameMigrationService
{
    private const string OldDbName = "ruoyu_study_docretrieval";
    private const string NewDbName = "ruoyu_study_doclibrary";

    public static async Task MigrateAsync(string? connectionString, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        // Only migrate for PostgreSQL (SQLite is just a file, no rename needed)
        if (!connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase)
            && !connectionString.Contains("Server=", StringComparison.OrdinalIgnoreCase))
            return;

        var csb = new DbConnectionStringBuilder { ConnectionString = connectionString };
        if (!csb.TryGetValue("Database", out var dbObj))
            return;

        var currentDbName = dbObj.ToString()!;

        // Build admin connection string (to postgres maintenance database)
        csb["Database"] = "postgres";
        var adminConnStr = csb.ConnectionString;

        if (currentDbName == OldDbName)
        {
            await CreateNewDatabaseFromTemplateAsync(adminConnStr, logger);
        }
        else if (currentDbName == NewDbName)
        {
            await DropOldDatabaseAsync(adminConnStr, logger);
        }
    }

    private static async Task CreateNewDatabaseFromTemplateAsync(string adminConnStr, ILogger logger)
    {
        await using var conn = new NpgsqlConnection(adminConnStr);
        await conn.OpenAsync();

        // Check if new DB already exists
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $"SELECT 1 FROM pg_database WHERE datname = '{NewDbName}'";
        var exists = await checkCmd.ExecuteScalarAsync();
        if (exists != null)
        {
            logger.LogInformation("Database {NewDb} already exists, skip migration", NewDbName);
            return;
        }

        // Check if old DB exists (if not, nothing to migrate)
        await using var oldCheckCmd = conn.CreateCommand();
        oldCheckCmd.CommandText = $"SELECT 1 FROM pg_database WHERE datname = '{OldDbName}'";
        var oldExists = await oldCheckCmd.ExecuteScalarAsync();
        if (oldExists == null)
        {
            logger.LogInformation("Old database {OldDb} does not exist, skip migration", OldDbName);
            return;
        }

        // Terminate all connections to old DB (except our own admin session)
        logger.LogInformation("Terminating connections to {OldDb} for template clone", OldDbName);
        await using var termCmd = conn.CreateCommand();
        termCmd.CommandText = $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{OldDbName}' AND pid <> pg_backend_pid()";
        await termCmd.ExecuteNonQueryAsync();

        // Brief delay to allow backends to actually terminate
        await Task.Delay(500);

        // Create new DB from template (copies schema + data atomically)
        logger.LogInformation("Creating database {NewDb} from template {OldDb}", NewDbName, OldDbName);
        await using var createCmd = conn.CreateCommand();
        createCmd.CommandText = $"CREATE DATABASE \"{NewDbName}\" TEMPLATE \"{OldDbName}\"";
        await createCmd.ExecuteNonQueryAsync();

        // Clear all Npgsql connection pools: the pg_terminate_backend above killed
        // pooled connections that EF Core may still hold, so they must be discarded
        // to prevent "terminating connection due to administrator command" errors.
        NpgsqlConnection.ClearAllPools();

        logger.LogInformation("Database migration completed: {OldDb} -> {NewDb}", OldDbName, NewDbName);
    }

    private static async Task DropOldDatabaseAsync(string adminConnStr, ILogger logger)
    {
        await using var conn = new NpgsqlConnection(adminConnStr);
        await conn.OpenAsync();

        // Check if old DB still exists
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $"SELECT 1 FROM pg_database WHERE datname = '{OldDbName}'";
        var exists = await checkCmd.ExecuteScalarAsync();
        if (exists == null)
        {
            logger.LogInformation("Old database {OldDb} does not exist, skip cleanup", OldDbName);
            return;
        }

        // Terminate all connections to old DB
        logger.LogInformation("Terminating connections to {OldDb} for cleanup", OldDbName);
        await using var termCmd = conn.CreateCommand();
        termCmd.CommandText = $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{OldDbName}' AND pid <> pg_backend_pid()";
        await termCmd.ExecuteNonQueryAsync();

        await Task.Delay(500);

        // Drop old DB
        logger.LogInformation("Dropping old database {OldDb}", OldDbName);
        await using var dropCmd = conn.CreateCommand();
        dropCmd.CommandText = $"DROP DATABASE \"{OldDbName}\"";
        await dropCmd.ExecuteNonQueryAsync();

        // Clear pools: terminate_backend killed any lingering connections to old DB
        NpgsqlConnection.ClearAllPools();

        logger.LogInformation("Old database {OldDb} dropped", OldDbName);
    }
}
