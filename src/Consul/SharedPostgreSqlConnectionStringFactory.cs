using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Doctheca.Consul;

public static class SharedPostgreSqlConnectionStringFactory
{
    private static readonly (string Setting, string Keyword, int Minimum)[] PoolSettings =
    [
        ("MaximumSize", "Maximum Pool Size", 1),
        ("MinimumSize", "Minimum Pool Size", 0),
        ("IdleLifetimeSeconds", "Connection Idle Lifetime", 0),
        ("PruningIntervalSeconds", "Connection Pruning Interval", 1),
        ("ConnectionLifetimeSeconds", "Connection Lifetime", 0),
        ("ConnectionTimeoutSeconds", "Timeout", 0),
        ("CommandTimeoutSeconds", "Command Timeout", 0),
    ];

    public static string? BuildOrFallback(IConfiguration configuration, string? fallbackConnectionString = null)
    {
        var host = configuration["PostgreSql:Host"];
        var username = configuration["PostgreSql:Username"];
        var databaseName = configuration["Database:Name"];
        var shared = !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(username)
            && !string.IsNullOrWhiteSpace(databaseName);
        if (!shared && string.IsNullOrWhiteSpace(fallbackConnectionString)) return fallbackConnectionString;

        try
        {
            var fallback = new DbConnectionStringBuilder { ConnectionString = fallbackConnectionString ?? string.Empty };
            var builder = shared ? new DbConnectionStringBuilder() : fallback;
            if (shared)
            {
                // Shared credentials remain authoritative; only pool tunables survive the fallback.
                foreach (string key in fallback.Keys)
                    if (PoolKeyword(key) is { } keyword) builder[keyword] = fallback[key];
                builder["Host"] = host!;
                builder["Port"] = string.IsNullOrWhiteSpace(configuration["PostgreSql:Port"]) ? "5432" : configuration["PostgreSql:Port"]!;
                builder["Database"] = databaseName!;
                builder["Username"] = username!;
                if (!string.IsNullOrWhiteSpace(configuration["PostgreSql:Password"])) builder["Password"] = configuration["PostgreSql:Password"]!;
            }
            foreach (var key in builder.Keys.Cast<string>().ToArray())
                if (PoolKeyword(key) is { } canonical && !string.Equals(key, canonical, StringComparison.OrdinalIgnoreCase))
                {
                    var value = builder[key]; builder.Remove(key); builder[canonical] = value;
                }
            foreach (var (setting, keyword, minimum) in PoolSettings)
            {
                var configured = configuration["Database:Pool:" + setting];
                if (configured is not null) builder[keyword] = Integer(configured, minimum);
                else if (Find(builder, keyword) is { } value) builder[keyword] = Integer(value, minimum);
            }
            if (Find(builder, "Maximum Pool Size") is { } maximum && Find(builder, "Minimum Pool Size") is { } minimumSize
                && Integer(minimumSize, 0) > Integer(maximum, 1)) throw Invalid();
            if (Find(builder, "Minimum Pool Size") is { } minimumOnly && Find(builder, "Maximum Pool Size") is null
                && Integer(minimumOnly, 0) > 100) throw Invalid();
            if (Find(builder, "Pooling") is { } pooling)
            {
                if (!bool.TryParse(pooling, out var enabled)) throw Invalid();
                builder["Pooling"] = enabled;
            }
            if (configuration["Database:ApplicationName"] is { } application)
            {
                if (string.IsNullOrWhiteSpace(application) || application.Any(char.IsControl)) throw Invalid();
                builder["Application Name"] = application;
            }
            return builder.ConnectionString;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
        { throw Invalid(); }
    }

    private static string? Find(DbConnectionStringBuilder builder, string keyword)
    {
        foreach (string key in builder.Keys)
            if (PoolKeyword(key) == keyword) return Convert.ToString(builder[key], CultureInfo.InvariantCulture);
        return null;
    }

    private static string? PoolKeyword(string key) => key.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "maxpoolsize" or "maximumpoolsize" => "Maximum Pool Size",
        "minpoolsize" or "minimumpoolsize" => "Minimum Pool Size",
        "connectionidlelifetime" => "Connection Idle Lifetime",
        "connectionpruninginterval" => "Connection Pruning Interval",
        "connectionlifetime" => "Connection Lifetime",
        "timeout" or "connectiontimeout" => "Timeout",
        "commandtimeout" => "Command Timeout",
        "pooling" => "Pooling",
        "applicationname" => "Application Name",
        _ => null,
    };

    private static int Integer(string value, int minimum) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= minimum
            ? parsed : throw Invalid();

    private static InvalidOperationException Invalid() => new("PostgreSQL connection pool configuration is invalid.");
}
