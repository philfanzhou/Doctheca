using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Doctheca.Host;

/// <summary>
/// The single logging composition entry point for the Doctheca host.
/// </summary>
/// <remarks>
/// Wires the mandatory-sanitizing ServiceMantle Serilog Console pipeline plus the optional
/// Grafana Loki remote sink. Contracts kept from the legacy pipeline:
/// <list type="bullet">
/// <item>The Loki address comes only from <c>Loki:Uri</c> (environment/command line &gt; Consul
/// &gt; appsettings precedence is provided by the configuration system). An empty value
/// disables the remote sink; Console logging stays enabled. The legacy
/// <c>Serilog:WriteTo:1:Args:uri</c> fallback no longer exists.</item>
/// <item>The fixed Loki stream label <c>service=Doctheca</c> is preserved so existing Grafana
/// queries keep working; the sink-owned <c>level</c> label is kept and no request/user/id
/// value is promoted to a stream label.</item>
/// <item>Plain-HTTP Loki endpoints are accepted through an explicit
/// <c>AllowInsecureHttp = true</c>: this is a deliberate acceptance of the existing
/// intranet HTTP deployment, not an automatic trust of the network. Use HTTPS whenever the
/// path crosses an untrusted network.</item>
/// <item>Minimum level Information with Warning overrides for <c>Microsoft.AspNetCore</c> and
/// <c>Microsoft.EntityFrameworkCore.Database.Command</c>, matching the retired
/// <c>Serilog:MinimumLevel</c> section. <c>Logging:LogLevel</c> in appsettings remains the
/// framework pre-filter and does not promise to cover the library pipeline floor.</item>
/// </list>
/// An invalid or credential-bearing <c>Loki:Uri</c> fails the host start with a safe library
/// error code (<c>loki.invalid_endpoint</c>) without echoing the configured value.
/// </remarks>
public static class DocthecaLoggingExtensions
{
    /// <summary>
    /// The fixed Loki stream label value. Deliberately distinct from the lowercase
    /// ServiceMantle ServiceId <c>doctheca</c>: this label is the existing Grafana query
    /// contract <c>{service="Doctheca"}</c>.
    /// </summary>
    public const string LokiServiceLabelValue = "Doctheca";

    /// <summary>
    /// Registers the ServiceMantle Serilog Console pipeline and the Grafana Loki remote sink.
    /// Must be called after the Consul configuration source is loaded so <c>Loki:Uri</c> from
    /// Consul KV is visible.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same builder.</returns>
    public static IHostApplicationBuilder AddDocthecaLogging(
        this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddServiceMantleSerilog(options =>
        {
            options.MinimumLevel = LogLevel.Information;
            options.MinimumLevelOverrides = new Dictionary<string, LogLevel>
            {
                ["Microsoft.AspNetCore"] = LogLevel.Warning,
                ["Microsoft.EntityFrameworkCore.Database.Command"] = LogLevel.Warning
            };
            options.IncludeScopes = true;
        });

        var address = builder.Configuration["Loki:Uri"];
        // An unparsable address is passed through as a null endpoint with the sink enabled so
        // the package validator fails the host start with its safe code instead of this call
        // site echoing the configured value.
        Uri? endpoint = null;
        if (!string.IsNullOrWhiteSpace(address))
        {
            Uri.TryCreate(address, UriKind.Absolute, out endpoint);
        }

        builder.AddServiceMantleGrafanaLoki(options =>
        {
            options.Enabled = !string.IsNullOrWhiteSpace(address);
            options.Endpoint = endpoint;
            // Explicit acceptance of the existing trusted-network HTTP Loki deployment
            // contract; ServiceMantle does not presume the network is trusted.
            options.AllowInsecureHttp = true;
            options.Labels = new Dictionary<string, string>
            {
                ["service"] = LokiServiceLabelValue
            };
        });

        return builder;
    }
}
