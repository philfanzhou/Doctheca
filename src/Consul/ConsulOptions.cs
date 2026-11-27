using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Doctheca.Consul;

public sealed class ConsulOptions
{
    public string Host { get; set; } = "host.docker.internal";

    public int Port { get; set; } = 8500;

    public string KvPrefix { get; set; } = "config/ruoyu";

    public int TimeoutMs { get; set; } = 3000;

    public int RetryCount { get; set; } = 3;

    public bool EnableCache { get; set; } = true;

    public string CacheDirectory { get; set; } = "./data/consul";

    public string? Token { get; set; }

    public static ConsulOptions Bind(IConfiguration config)
    {
        var options = new ConsulOptions();
        config.GetSection("Consul").Bind(options);

        var envHttpAddr = Environment.GetEnvironmentVariable("CONSUL_HTTP_ADDR");
        if (!string.IsNullOrWhiteSpace(envHttpAddr))
        {
            ApplyHttpAddressOverride(options, envHttpAddr);
        }

        var envHost = Environment.GetEnvironmentVariable("CONSUL_HOST");
        if (!string.IsNullOrWhiteSpace(envHost))
        {
            options.Host = envHost;
        }

        var envPort = Environment.GetEnvironmentVariable("CONSUL_PORT");
        if (int.TryParse(envPort, out var port))
        {
            options.Port = port;
        }

        var envKvPrefix = Environment.GetEnvironmentVariable("CONSUL_KV_PREFIX");
        if (!string.IsNullOrWhiteSpace(envKvPrefix))
        {
            options.KvPrefix = envKvPrefix;
        }

        var envTimeout = Environment.GetEnvironmentVariable("CONSUL_TIMEOUT_MS");
        if (int.TryParse(envTimeout, out var timeout))
        {
            options.TimeoutMs = timeout;
        }

        var envRetry = Environment.GetEnvironmentVariable("CONSUL_RETRY_COUNT");
        if (int.TryParse(envRetry, out var retry))
        {
            options.RetryCount = retry;
        }

        var envEnableCache = Environment.GetEnvironmentVariable("CONSUL_ENABLE_CACHE");
        if (bool.TryParse(envEnableCache, out var enableCache))
        {
            options.EnableCache = enableCache;
        }

        var envCacheDir = Environment.GetEnvironmentVariable("CONSUL_CACHE_DIR");
        if (!string.IsNullOrWhiteSpace(envCacheDir))
        {
            options.CacheDirectory = envCacheDir;
        }

        var envToken = Environment.GetEnvironmentVariable("CONSUL_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            options.Token = envToken;
        }

        return options;
    }

    private static void ApplyHttpAddressOverride(ConsulOptions options, string httpAddress)
    {
        var normalized = httpAddress.Trim();
        if (Uri.TryCreate($"http://{normalized}", UriKind.Absolute, out var hostPortUri))
        {
            options.Host = hostPortUri.Host;
            if (!hostPortUri.IsDefaultPort)
            {
                options.Port = hostPortUri.Port;
            }

            return;
        }

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var absoluteUri) &&
            !string.IsNullOrWhiteSpace(absoluteUri.Host))
        {
            options.Host = absoluteUri.Host;
            if (!absoluteUri.IsDefaultPort)
            {
                options.Port = absoluteUri.Port;
            }

            return;
        }

        var lastColonIndex = normalized.LastIndexOf(':');
        if (lastColonIndex > 0 &&
            lastColonIndex < normalized.Length - 1 &&
            int.TryParse(normalized[(lastColonIndex + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            options.Host = normalized[..lastColonIndex];
            options.Port = port;
            return;
        }

        options.Host = normalized;
    }
}
