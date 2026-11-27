using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace Doctheca.Consul;

public static class ConfigurationExtensions
{
    public static IConfigurationBuilder AddConsulConfiguration(
        this IConfigurationBuilder builder,
        IConfiguration config)
    {
        var options = ConsulOptions.Bind(config);
        var prefixes = KvLoader.BuildPrefixes(options);
        StartupDiagnosticsFormatter.WriteBootstrap(
            $"Consul KV load begin: Address={options.Host}:{options.Port}, Prefixes={StartupDiagnosticsFormatter.SummarizePrefixes(prefixes)}, TimeoutMs={options.TimeoutMs}, RetryCount={options.RetryCount}, Cache={options.EnableCache}, Token={StartupDiagnosticsFormatter.MaskSecret(options.Token)}");

        if (!string.IsNullOrWhiteSpace(options.Token))
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Consul:Token"] = options.Token
            });
        }

        var cacheService = new SnapshotCache(options.CacheDirectory, $"{options.Host}:{options.Port}");
        try
        {
            var result = new KvLoader(options).Load();
            ApplySnapshotWithExpectedPrecedence(builder, result.Snapshot);
            if (options.EnableCache && result.Snapshot.Count > 0)
            {
                cacheService.Save(result.Snapshot);
            }

            RuntimeState.Instance.MarkLoaded("Consul", result.Snapshot.Count, result.Prefixes, options.CacheDirectory);
            StartupDiagnosticsFormatter.WriteBootstrap(
                $"Consul KV load success: Source=Consul, KeyCount={result.Snapshot.Count}, Prefixes={StartupDiagnosticsFormatter.SummarizePrefixes(result.Prefixes)}, CacheDirectory={options.CacheDirectory}");
            return builder;
        }
        catch (Exception ex)
        {
            StartupDiagnosticsFormatter.WriteBootstrap(
                $"Consul KV load failed: Address={options.Host}:{options.Port}, Prefixes={StartupDiagnosticsFormatter.SummarizePrefixes(prefixes)}, Error={StartupDiagnosticsFormatter.SummarizeError(ex.Message)}");

            if (options.EnableCache)
            {
                try
                {
                    var cached = cacheService.Load();
                    if (cached != null && cached.Count > 0)
                    {
                        ApplySnapshotWithExpectedPrecedence(builder, cached);
                        RuntimeState.Instance.MarkFallback("Cache", ex.Message, cached.Count, prefixes, options.CacheDirectory);
                        StartupDiagnosticsFormatter.WriteBootstrap(
                            $"Consul KV fallback: Source=Cache, KeyCount={cached.Count}, CacheDirectory={options.CacheDirectory}, Error={StartupDiagnosticsFormatter.SummarizeError(ex.Message)}");
                        return builder;
                    }
                }
                catch (Exception cacheEx)
                {
                    var mergedError = $"{ex.Message}; cache load failed: {cacheEx.Message}";
                    RuntimeState.Instance.MarkFallback("AppSettings", mergedError, 0, prefixes, options.CacheDirectory);
                    StartupDiagnosticsFormatter.WriteBootstrap(
                        $"Consul KV fallback: Source=AppSettings, CacheDirectory={options.CacheDirectory}, Error={StartupDiagnosticsFormatter.SummarizeError(mergedError)}");
                    return builder;
                }
            }

            RuntimeState.Instance.MarkFallback("AppSettings", ex.Message, 0, prefixes, options.CacheDirectory);
            StartupDiagnosticsFormatter.WriteBootstrap(
                $"Consul KV fallback: Source=AppSettings, CacheDirectory={options.CacheDirectory}, Error={StartupDiagnosticsFormatter.SummarizeError(ex.Message)}");
            return builder;
        }
        finally
        {
            cacheService.Dispose();
        }
    }

    public static void ApplySnapshotWithExpectedPrecedence(
        IConfigurationBuilder builder,
        IDictionary<string, string?> snapshot)
    {
        if (snapshot.Count == 0)
        {
            return;
        }

        var replaySources = builder.Sources
            .Where(static source =>
                source is EnvironmentVariablesConfigurationSource ||
                source is CommandLineConfigurationSource)
            .ToArray();

        builder.AddInMemoryCollection(snapshot);

        foreach (var replaySource in replaySources)
        {
            builder.Add(replaySource);
        }
    }
}
