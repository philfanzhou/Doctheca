using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Doctheca.Database;
using Testcontainers.PostgreSql;
using Xunit;

namespace Doctheca.Tests.Database;

/// <summary>
/// One shared PostgreSQL 16 container for the migration integration collection. Every test gets
/// its own database inside the container, so the golden states never interfere. The tests run on
/// any Docker-capable machine and in GitHub-hosted CI without being skipped by default.
/// </summary>
public sealed class PostgreSqlMigrationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container;

    /// <summary>
    /// A random per-run secret used as the container password. Tests assert this canary never
    /// appears in exception messages or log output. It is generated per fixture and is not a
    /// credential of any real environment.
    /// </summary>
    public string PasswordCanary { get; } = $"canary-{Guid.NewGuid():N}";

    public PostgreSqlMigrationFixture()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithCommand("-c", "shared_preload_libraries=pg_stat_statements")
            .WithDatabase("postgres")
            .WithUsername("doctheca_migrator")
            .WithPassword(PasswordCanary)
            .Build();
    }

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// A connection string for one database inside the container, pooling disabled so drops and
    /// catalog checks are never masked by pooled physical connections.
    /// </summary>
    public string GetConnectionString(string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = database,
            Pooling = false,
        };
        return builder.ConnectionString;
    }

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"mig_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(GetConnectionString("postgres"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{name}\"";
        await command.ExecuteNonQueryAsync();
        return name;
    }

    public async Task DropDatabaseAsync(string database)
    {
        await using var connection = new NpgsqlConnection(GetConnectionString("postgres"));
        await connection.OpenAsync();
        await using var terminate = connection.CreateCommand();
        terminate.CommandText = """
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = @name AND pid <> pg_backend_pid()
            """;
        terminate.Parameters.AddWithValue("@name", database);
        await terminate.ExecuteNonQueryAsync();
        await using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{database}\"";
        await drop.ExecuteNonQueryAsync();
    }

    public DocthecaDbContext CreateContext(string database) =>
        new(new DbContextOptionsBuilder<DocthecaDbContext>()
            .UseNpgsql(GetConnectionString(database))
            .Options);

    public DocthecaDbContext CreateContextWithConnectionString(string connectionString) =>
        new(new DbContextOptionsBuilder<DocthecaDbContext>()
            .UseNpgsql(connectionString)
            .Options);
}

/// <summary>
/// All container-backed migration tests live in this single collection: xUnit runs collections in
/// parallel, and grouping them keeps concurrent hosts from racing container startup.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MigrationIntegrationCollection : ICollectionFixture<PostgreSqlMigrationFixture>
{
    public const string Name = "migration-integration";
}

/// <summary>
/// Shared helpers for the migration tests: golden database states, history reads, and secret-free
/// state capture.
/// </summary>
internal static class MigrationGoldenStates
{
    internal const string InitialId = DocthecaMigrationExecutor.InitialCreateMigrationId;
    internal const string HistoryTable = DocthecaMigrationExecutor.HistoryTableName;

    internal static readonly Guid FileId = Guid.Parse("a1111111-1111-4111-8111-111111111111");
    internal static readonly Guid ParseId = Guid.Parse("b2222222-2222-4222-8222-222222222222");
    internal static readonly Guid ImageId = Guid.Parse("c3333333-3333-4333-8333-333333333333");
    internal static readonly Guid BlockId = Guid.Parse("d4444444-4444-4444-8444-444444444444");

    /// <summary>
    /// The exact trigger DDL the retired initializer applied on every startup; used to build the
    /// realistic full-legacy state.
    /// </summary>
    internal const string TriggerSql = """
        CREATE OR REPLACE FUNCTION set_document_files_updated_at()
        RETURNS TRIGGER AS $$
        BEGIN
            NEW.updated_at = NOW();
            RETURN NEW;
        END;
        $$ LANGUAGE plpgsql;
        CREATE TRIGGER set_document_files_updated_at
            BEFORE UPDATE ON document_files
            FOR EACH ROW
            EXECUTE FUNCTION set_document_files_updated_at();
        """;

    internal static Task EnsureCreatedLegacyAsync(DocthecaDbContext context) =>
        context.Database.EnsureCreatedAsync();

    internal static Task ExecuteRawAsync(DocthecaDbContext context, string sql) =>
        context.Database.ExecuteSqlRawAsync(sql);

    internal const string EmptyJsonb = "{}";

    internal static async Task SeedBusinessRowsAsync(DocthecaDbContext context)
    {
        await ExecuteRawAsync(context, $$"""
            INSERT INTO document_files (id, file_name, file_path, structadoc_document_id, content_type,
                                        created_by, created_at, updated_at, subject, grade, year)
            VALUES ('{{FileId}}', 'legacy-doc.pdf', 'legacy/path.pdf', NULL, 'application/pdf',
                    NULL, '2026-01-01T00:00:00Z', '2026-01-02T00:00:00Z', 'math', 'g7', '2025');
            INSERT INTO document_parses (id, document_file_id, model_version, status, external_task_id,
                                         structadoc_parse_run_id, markdown_content, content_list,
                                         content_list_v2, model_json, layout_json, zip_path, error_message, parsed_at)
            VALUES ('{{ParseId}}', '{{FileId}}', 'vlm', 'succeeded', NULL, NULL, '# legacy', NULL,
                    NULL, NULL, NULL, NULL, NULL, '2026-01-02T00:00:00Z');
            INSERT INTO document_parse_images (id, parse_id, image_name, image_path, content_type)
            VALUES ('{{ImageId}}', '{{ParseId}}', 'legacy-img', 'legacy/img.jpg', 'image/jpeg');
            """);
        // The block row carries a jsonb object value: EF's raw-SQL facade applies composite
        // format parsing to the command text, so the literal goes through the interpolated
        // (parameterized) overload instead of the raw string.
        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO document_parse_blocks (id, parse_id, page_id, sort_index, block_type, text_content,
                                               image_id, block_data, created_at, sub_type, text_level,
                                               text_format, bbox_x0, bbox_y0, bbox_x1, bbox_y1, score, caption)
            VALUES ({BlockId}, {ParseId}, 1, 0, 'text', 'legacy text', NULL, {EmptyJsonb}::jsonb,
                    '2026-01-02T00:00:00Z', NULL, -1, '', NULL, NULL, NULL, NULL, NULL, NULL)
            """);
    }

    internal static async Task<List<string>> ReadAppliedHistoryAsync(DocthecaDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT \"MigrationId\" FROM \"{HistoryTable}\" ORDER BY \"MigrationId\"";
            var ids = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetString(0));
            }

            return ids;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    /// <summary>
    /// The column/type/nullability shape of the four business tables, read from the catalogs;
    /// used for the EnsureCreated-versus-migrated comparison and structure assertions.
    /// </summary>
    internal static async Task<List<string>> ReadColumnShapeAsync(DocthecaDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            var shape = new List<string>();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN pg_attribute a ON a.attrelid = c.oid
                WHERE n.nspname = 'public' AND c.relkind = 'r'
                  AND c.relname = ANY(@tables)
                  AND a.attnum > 0 AND NOT a.attisdropped
                ORDER BY c.relname, a.attnum
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@tables";
            parameter.Value = DocthecaMigrationExecutor.KnownTableNames.ToArray();
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                shape.Add($"{reader.GetString(0)}.{reader.GetString(1)}:{reader.GetString(2)}:{(reader.GetBoolean(3) ? "notnull" : "null")}");
            }

            return shape;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    /// <summary>
    /// The constraint/index shape (primary keys, foreign keys with delete rules, non-constraint
    /// indexes) of the four business tables.
    /// </summary>
    internal static async Task<List<string>> ReadConstraintShapeAsync(DocthecaDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            var shape = new List<string>();
            await using (var constraints = connection.CreateCommand())
            {
                constraints.CommandText = """
                    SELECT c.relname, con.conname, con.contype::text, con.confdeltype::text, fc.relname,
                           (SELECT array_agg(a.attname ORDER BY k.ord)
                              FROM unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord)
                              JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum)
                    FROM pg_constraint con
                    JOIN pg_class c ON c.oid = con.conrelid
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    LEFT JOIN pg_class fc ON fc.oid = con.confrelid
                    WHERE n.nspname = 'public'
                      AND c.relname = ANY(@tables)
                      AND con.contype = ANY(ARRAY['p', 'f']::"char"[])
                    ORDER BY c.relname, con.conname
                    """;
                var parameter = constraints.CreateParameter();
                parameter.ParameterName = "@tables";
                parameter.Value = DocthecaMigrationExecutor.KnownTableNames.ToArray();
                constraints.Parameters.Add(parameter);
                await using var reader = await constraints.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var columns = reader.IsDBNull(5) ? "" : string.Join(",", reader.GetFieldValue<string[]>(5));
                    shape.Add($"{reader.GetString(1)}:{reader.GetString(2)}:{reader.GetString(0)}:{columns}:{reader.GetString(3)}:{(reader.IsDBNull(4) ? "" : reader.GetString(4))}");
                }
            }

            await using (var indexes = connection.CreateCommand())
            {
                indexes.CommandText = """
                    SELECT c.relname, ic.relname, i.indisunique,
                           (SELECT array_agg(a.attname ORDER BY k.ord)
                              FROM unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord)
                              JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum)
                    FROM pg_index i
                    JOIN pg_class ic ON ic.oid = i.indexrelid
                    JOIN pg_class c ON c.oid = i.indrelid
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'public'
                      AND c.relname = ANY(@tables)
                      AND NOT i.indisprimary
                      AND NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid = i.indexrelid)
                    ORDER BY c.relname, ic.relname
                    """;
                var parameter = indexes.CreateParameter();
                parameter.ParameterName = "@tables";
                parameter.Value = DocthecaMigrationExecutor.KnownTableNames.ToArray();
                indexes.Parameters.Add(parameter);
                await using var reader = await indexes.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var columns = reader.IsDBNull(3) ? "" : string.Join(",", reader.GetFieldValue<string[]>(3));
                    shape.Add($"{reader.GetString(1)}:index:{reader.GetString(0)}:{columns}:{reader.GetBoolean(2)}");
                }
            }

            return shape;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    internal static async Task<long> CountRowsAsync(DocthecaDbContext context, string table)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM \"{table}\"";
            return (long)(await command.ExecuteScalarAsync())!;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    internal static async Task<T?> ScalarAsync<T>(DocthecaDbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = await command.ExecuteScalarAsync();
            return value is null or DBNull ? default : (T)value;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}

/// <summary>
/// Captures formatted log messages so tests can assert secret canaries never enter log output.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _messages = [];

    internal IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages)
            {
                return _messages.ToList();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);

    public void Dispose()
    {
    }

    private sealed class CaptureLogger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (owner._messages)
            {
                owner._messages.Add(formatter(state, exception) + " " + exception);
            }
        }
    }
}
