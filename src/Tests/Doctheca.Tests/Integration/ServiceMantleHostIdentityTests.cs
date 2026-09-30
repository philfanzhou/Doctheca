using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ServiceMantle;
using ServiceMantle.Bootstrap;
using ServiceMantle.Web.Logging;
using Doctheca.Consul;
using Xunit;

namespace Doctheca.Tests.Integration;

/// <summary>
/// Boots the real Program.cs entry point (WebApplicationFactory + Testcontainers PostgreSQL)
/// and proves the ServiceMantle foundation wiring: the host starts with valid instrumentation
/// registrations, the identity resolves as configured, every host build gets a fresh
/// InstanceId, base telemetry stays exporter-free inside the process, and the foundation
/// produces no Bootstrap/installation artifacts and no Consul Catalog writes.
/// </summary>
[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed partial class ServiceMantleHostIdentityTests : ServiceMantleIntegrationTestBase
{
    public ServiceMantleHostIdentityTests(PostgreSqlFixture database) : base(database)
    {
    }

    [GeneratedRegex("^doctheca-[0-9a-f]{32}$")]
    private static partial Regex InstanceIdPattern();

    [Fact]
    public void Host_Starts_AndResolvesServiceMantleIdentity()
    {
        // Accessing factory.Services runs the full host startup. The library's
        // OpenTelemetryRegistrationValidator hosted service fails the startup when the
        // instrumentation registrations are invalid or conflicting, so reaching the assertions
        // proves both the host start and the registration validity.
        using var factory = CreateFactory();

        var serviceId = factory.Services.GetRequiredService<ServiceId>();
        var instanceId = factory.Services.GetRequiredService<InstanceId>();
        var logContext = factory.Services.GetRequiredService<ServiceLogContext>();

        Assert.Equal("doctheca", serviceId.Value);
        Assert.Matches(InstanceIdPattern(), instanceId.Value);

        Assert.Equal(serviceId.Value, logContext.ServiceName);
        Assert.Equal(instanceId.Value, logContext.InstanceId);
        Assert.Equal(ExpectedEntryAssemblyServiceVersion(), logContext.ServiceVersion);
    }

    [Fact]
    public void TwoHostBuilds_GetDifferentInstanceIds()
    {
        using var first = CreateFactory();
        using var second = CreateFactory();

        var firstInstanceId = first.Services.GetRequiredService<InstanceId>();
        var secondInstanceId = second.Services.GetRequiredService<InstanceId>();

        // The InstanceId is regenerated on every host build and is not a persistent identity.
        Assert.Matches(InstanceIdPattern(), firstInstanceId.Value);
        Assert.Matches(InstanceIdPattern(), secondInstanceId.Value);
        Assert.NotEqual(firstInstanceId.Value, secondInstanceId.Value);
    }

    [Fact]
    public void Foundation_RegistersInstrumentation_WithoutExportersOrDiscovery()
    {
        IReadOnlyList<ServiceDescriptor>? snapshot = null;
        using var factory = CreateFactory(configureTestServices: services =>
        {
            // ConfigureTestServices runs on the app's real service collection before the
            // provider is built; snapshot it to inspect what the foundation registered.
            snapshot = services.ToArray();
        });
        // Force the full host startup before inspecting the snapshot.
        _ = factory.Services;

        Assert.NotNull(snapshot);

        // No telemetry exporter of any kind is registered: with the foundation slice,
        // telemetry never leaves the process (no OTLP, no Prometheus, no console exporter).
        Assert.DoesNotContain(snapshot!, descriptor =>
            IsExporterRegistration(descriptor.ServiceType) ||
            IsExporterRegistration(descriptor.ImplementationType));

        // No Consul service-discovery registrar is registered, so nothing in the foundation
        // can issue Consul Catalog write operations; the KV configuration loader is read-only
        // and pointed at a closed loopback port by the fixture.
        Assert.DoesNotContain(snapshot, descriptor =>
            InNamespace(descriptor.ServiceType, "Steeltoe.Discovery") ||
            InNamespace(descriptor.ImplementationType, "Steeltoe.Discovery"));
        Assert.NotEqual("Consul", RuoyuConsulRuntimeState.Instance.Source);
    }

    [Fact]
    public void OpenTelemetryProviders_Resolve_WithServiceMantleIdentityResource()
    {
        using var factory = CreateFactory();

        var logContext = factory.Services.GetRequiredService<ServiceLogContext>();
        var tracerProvider = factory.Services.GetService<TracerProvider>();
        var meterProvider = factory.Services.GetService<MeterProvider>();

        Assert.NotNull(tracerProvider);
        Assert.NotNull(meterProvider);

        // The resource carries exactly the ServiceMantle identity: service name, version, and
        // the per-build instance id (AddService on an empty resource builder). The SDK provider
        // types are internal in OpenTelemetry 1.x, so the Resource is read reflectively.
        AssertResourceIdentity(ReadResourceAttributes(tracerProvider!), logContext);
        AssertResourceIdentity(ReadResourceAttributes(meterProvider!), logContext);
    }

    [Fact]
    public void Foundation_CreatesNoBootstrapFile_AndNoServiceMantleTables()
    {
        using var factory = CreateFactory();

        // The Bootstrap store stays lazy: the resolved default path must not exist on disk
        // after a full host startup, and no installation file is produced anywhere.
        var bootstrapStore = factory.Services.GetRequiredService<BootstrapFileStore>();
        Assert.False(
            File.Exists(bootstrapStore.FilePath),
            $"The foundation must not write a Bootstrap file, but found {bootstrapStore.FilePath}.");

        // DocthecaMigrationExecutor migrates the Doctheca schema from the baseline (snake_case naming).
        // ServiceMantle adds no schema of its own in this slice: no
        // bootstrap/installation/management tables exist.
        var tables = ReadTableNames();
        Assert.Contains("document_files", tables);
        Assert.DoesNotContain(
            tables,
            table => table.Contains("bootstrap", StringComparison.OrdinalIgnoreCase) ||
                table.Contains("installation", StringComparison.OrdinalIgnoreCase) ||
                table.Contains("mantle", StringComparison.OrdinalIgnoreCase) ||
                table.Contains("service_setting", StringComparison.OrdinalIgnoreCase) ||
                table.Contains("management_audit", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<KeyValuePair<string, object>> ReadResourceAttributes(object provider)
    {
        var resource = provider.GetType()
            .GetProperty("Resource", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
            .GetValue(provider);
        Assert.NotNull(resource);

        var attributes = resource!.GetType()
            .GetProperty("Attributes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
            .GetValue(resource) as IEnumerable<KeyValuePair<string, object>>;
        Assert.NotNull(attributes);
        return attributes!;
    }

    private static void AssertResourceIdentity(
        IEnumerable<KeyValuePair<string, object>> attributes,
        ServiceLogContext logContext)
    {
        var materialized = attributes.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(logContext.ServiceName, materialized["service.name"]);
        Assert.Equal(logContext.ServiceVersion, materialized["service.version"]);
        Assert.Equal(logContext.InstanceId, materialized["service.instance.id"]);
    }

    private List<string> ReadTableNames()
    {
        var tables = new List<string>();
        using var connection = new NpgsqlConnection(Database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static bool IsExporterRegistration(Type? type) =>
        InNamespace(type, "OpenTelemetry.Exporters") ||
        (type is not null &&
            (type.FullName!.Contains(".Otlp", StringComparison.Ordinal) ||
                type.FullName.Contains(".Prometheus", StringComparison.Ordinal)));

    private static bool InNamespace(Type? type, string namespacePrefix) =>
        type?.Namespace?.StartsWith(namespacePrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// Mirrors the library's entry-assembly version resolution (AddServiceMantle is called
    /// without an explicit serviceVersion): informational version, then assembly version, then
    /// "unknown". Under WebApplicationFactory the entry assembly is the test host, so the
    /// assertion proves the resolution rule rather than a hard-coded version.
    /// </summary>
    private static string ExpectedEntryAssemblyServiceVersion()
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        var informationalVersion = entryAssembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Trim();
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        var assemblyVersion = entryAssembly?.GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(assemblyVersion) ? "unknown" : assemblyVersion;
    }
}
