using Microsoft.Extensions.DependencyInjection;
using ServiceMantle;
using ServiceMantle.Web;

namespace Doctheca.Host;

/// <summary>
/// The single ServiceMantle host foundation entry point for Doctheca.
/// </summary>
/// <remarks>
/// This extension is the only place the ServiceMantle registration root and the per-host
/// <see cref="InstanceId"/> are created. Later ServiceMantle migration slices must extend the
/// returned <see cref="ServiceMantleBuilder"/> instead of calling <c>AddServiceMantle</c> again
/// or regenerating the instance identity:
/// <list type="bullet">
/// <item>ServiceId <c>doctheca</c> is the stable deployment identity (lowercase; deliberately
/// distinct from the fixed Loki stream label <c>Doctheca</c>).</item>
/// <item>InstanceId <c>doctheca-&lt;Guid:N&gt;</c> is regenerated on every host build and is NOT
/// a persistent identity: it changes on each restart.</item>
/// <item>No bootstrapFilePath is passed: the bootstrap store stays a lazy singleton and this
/// wiring performs zero disk writes.</item>
/// <item>No serviceVersion is passed: it resolves from the entry assembly informational
/// version.</item>
/// <item><c>AddOpenTelemetryInstrumentation</c> uses the default options (ASP.NET Core /
/// HttpClient tracing and runtime metrics) and registers NO exporter: telemetry never leaves
/// the process in this slice.</item>
/// </list>
/// Only lazy foundation services are registered; Bootstrap is never read or written here, and
/// management, setup, discovery registration, and startup-phase gating stay disabled.
/// </remarks>
public static class DocthecaServiceMantleExtensions
{
    /// <summary>
    /// The stable ServiceMantle service identity of this deployment.
    /// </summary>
    public const string ServiceIdValue = "doctheca";

    /// <summary>
    /// Registers the ServiceMantle host foundation (service identity, correlation ID support,
    /// base OpenTelemetry instrumentation without exporters) exactly once per host build.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <returns>The builder later ServiceMantle slices extend with optional capabilities.</returns>
    public static ServiceMantleBuilder AddDocthecaServiceMantleFoundation(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services
            .AddServiceMantle(
                ServiceId.Parse(ServiceIdValue),
                InstanceId.Parse($"{ServiceIdValue}-{Guid.NewGuid():N}"))
            .AddOpenTelemetryInstrumentation();
    }
}
