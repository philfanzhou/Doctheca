using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle.Logging.Pipeline;
using ServiceMantle.Web.Logging;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Host;
using Xunit;

namespace Doctheca.Tests.Integration;

/// <summary>
/// Real-host logging migration proofs: the ServiceMantle Serilog pipeline writes the same
/// filtered, sanitized events to Console and to a local fake Loki receiver under the fixed
/// stream label <c>service=Doctheca</c>; framework Information noise is suppressed while
/// Warning survives; synthetic secrets and exception message/Data canaries never reach either
/// sink while normal structured fields do; an empty <c>Loki:Uri</c> disables the remote sink;
/// invalid or credential-bearing URIs fail the host start with a safe code and no echo; an
/// unreachable/503 Loki never blocks business requests; startup and background-worker logs
/// carry the ServiceLogContext identity scope without an HTTP CorrelationId while concurrent
/// requests keep their own; and shutdown stays bounded. No test talks to a real Loki.
/// </summary>
[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed class ServiceMantleLoggingTests : ServiceMantleIntegrationTestBase
{
    public ServiceMantleLoggingTests(PostgreSqlFixture database) : base(database)
    {
    }

    [Fact]
    public async Task RealHost_ConsoleAndLokiShareFilteredSanitizedRequestEvents()
    {
        var batches = new ConcurrentQueue<string>();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var loki = await StartFakeLokiAsync(async context =>
        {
            batches.Enqueue(await new StreamReader(context.Request.Body).ReadToEndAsync());
            received.TrySetResult();
            context.Response.StatusCode = 204;
        });
        var address = ServerAddress(loki);

        var original = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            using var factory = CreateFactory(
                configureTestServices: services =>
                {
                    services.RemoveAll<ISearchIndexService>();
                    services.AddSingleton<ISearchIndexService>(sp =>
                        new LoggingProbeSearchIndex(sp.GetRequiredService<ILoggerFactory>()));
                },
                settings: new Dictionary<string, string?> { ["Loki:Uri"] = address });
            using var client = factory.CreateClient();
            var request = new HttpRequestMessage(
                HttpMethod.Get, "/admin/documents/search?query=probe");
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", CreateToken("admin"));
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var correlation = response.Headers.GetValues(CorrelationHeaderName).Single();
            var logContext = factory.Services.GetRequiredService<ServiceLogContext>();

            // Batching is asynchronous; wait for the request event, not just startup events.
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!batches.Any(batch => batch.Contains("probe.application")))
            {
                await Task.Delay(20, timeout.Token);
            }

            var lines = batches
                .SelectMany(batch => JsonDocument.Parse(batch).RootElement
                    .GetProperty("streams").EnumerateArray().SelectMany(stream =>
                    {
                        // The fixed query contract: {service="Doctheca"} plus the sink-owned
                        // level label; nothing else is promoted to a stream label.
                        Assert.Equal(
                            DocthecaLoggingExtensions.LokiServiceLabelValue,
                            stream.GetProperty("stream").GetProperty("service").GetString());
                        Assert.True(stream.GetProperty("stream").TryGetProperty("level", out _));
                        return stream.GetProperty("values").EnumerateArray()
                            .Select(value => value[1].GetString()!).ToArray();
                    }))
                .ToArray();
            var remote = string.Join('\n', lines);
            var console = output.ToString();

            foreach (var actual in new[] { console, remote })
            {
                // Same filtered event set on both sinks.
                Assert.Contains("probe.application", actual);
                Assert.Contains("probe.aspnet.warning", actual);
                Assert.Contains("probe.ef.warning", actual);
                Assert.DoesNotContain("probe.aspnet.information", actual);
                Assert.DoesNotContain("probe.ef.information", actual);

                // Denied structured fields (by name fragment, values are neutral canaries),
                // the {FreeTextSecret} field (denied "secret" name fragment), and the
                // exception message/Data canaries are all gone; normal fields survive.
                Assert.DoesNotContain(LoggingProbeSearchIndex.DeniedFieldCanary1, actual);
                Assert.DoesNotContain(LoggingProbeSearchIndex.DeniedFieldCanary2, actual);
                Assert.DoesNotContain(LoggingProbeSearchIndex.DeniedFieldCanary3, actual);
                Assert.DoesNotContain(LoggingProbeSearchIndex.DeniedFieldCanary4, actual);
                Assert.DoesNotContain(LoggingProbeSearchIndex.DeniedFieldCanary5, actual);
                Assert.DoesNotContain(LoggingProbeSearchIndex.DeniedFieldCanary6, actual);
                Assert.DoesNotContain(LoggingProbeSearchIndex.ExceptionMessageCanary, actual);
                Assert.DoesNotContain(LoggingProbeSearchIndex.ExceptionDataCanary, actual);
                Assert.DoesNotContain(LoggingProbeSearchIndex.FreeTextCanary, actual);
                Assert.Contains(LoggingProbeSearchIndex.NormalFieldMarker, actual);
                Assert.Contains("probe.exception", actual);

                // The request event carries the per-request CorrelationId plus the
                // ServiceMantle identity fields; the retired MachineName/ThreadId enrichers
                // are gone.
                var requestLine = actual
                    .Split('\n')
                    .First(line => line.Contains("probe.application"));
                Assert.Contains(correlation, requestLine);
                Assert.Contains(logContext.ServiceName, requestLine);
                Assert.Contains(logContext.ServiceVersion, requestLine);
                Assert.Contains(logContext.InstanceId, requestLine);
                Assert.DoesNotContain("MachineName", requestLine);
                Assert.DoesNotContain("ThreadId", requestLine);
            }
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public async Task UnreachableLoki_RetriesWithoutBlockingBusinessRequests()
    {
        var attempts = 0;
        await using var loki = await StartFakeLokiAsync(context =>
        {
            Interlocked.Increment(ref attempts);
            context.Response.StatusCode = 503;
            return Task.CompletedTask;
        });
        var address = ServerAddress(loki);

        using var factory = CreateFactory(
            settings: new Dictionary<string, string?> { ["Loki:Uri"] = address });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (Volatile.Read(ref attempts) < 2)
        {
            await Task.Delay(20, timeout.Token);
        }

        // Delivery keeps retrying in the background while the business endpoint stays fast.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Theory]
    [InlineData("relative/canary")]
    [InlineData("http://user:canary@127.0.0.1:3100")]
    [InlineData("https://example.test?token=canary")]
    [InlineData("https://example.test#canary")]
    public async Task InvalidLokiUri_FailsHostStart_WithSafeCodeAndNoEcho(string uri)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration["Loki:Uri"] = uri;
        builder.AddDocthecaLogging();
        using var host = builder.Build();

        var failure = await Assert.ThrowsAsync<SerilogConfigurationException>(
            () => host.StartAsync());
        Assert.Equal("loki.invalid_endpoint", failure.ErrorCode);
        Assert.DoesNotContain("canary", failure.ToString());
    }

    [Fact]
    public async Task EmptyLokiUri_DisablesRemoteSink_AndHostStarts()
    {
        IReadOnlyList<ServiceDescriptor>? snapshot = null;
        // The fixture default is Loki:Uri="" : Console stays on, no remote sink is created,
        // and no legacy Serilog configuration section survives anywhere.
        using var factory = CreateFactory(configureTestServices: services =>
        {
            snapshot = services.ToArray();
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Null(factory.Services.GetRequiredService<IConfiguration>()["Serilog:WriteTo:1:Args:uri"]);
        Assert.Null(factory.Services.GetRequiredService<IConfiguration>()["Serilog:MinimumLevel:Default"]);

        var sinkFactory = Assert.Single(
            snapshot!,
            descriptor => descriptor.ServiceType.Name == "ISerilogSinkFactory");
        Assert.Equal("ConsoleSinkFactory", sinkFactory.ImplementationType?.Name);
    }

    [Fact]
    public async Task StartupAndWorker_UseIdentityScopes_WithoutRequestCorrelationId()
    {
        var capture = new RequestScopeCapture();
        var factory = CreateFactory(configureTestServices: services =>
        {
            services.RemoveAll<ILoggerFactory>();
            services.AddSingleton<ILoggerFactory>(
                _ => LoggerFactory.Create(logging => logging.AddProvider(capture)));
        });
        try
        {
            using var client = factory.CreateClient();
            var logContext = factory.Services.GetRequiredService<ServiceLogContext>();

            // Two concurrent requests, each with its own correlation id.
            var responses = await Task.WhenAll(new[] { "log-iso-a", "log-iso-b" }.Select(
                async id =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
                    request.Headers.Add(CorrelationHeaderName, id);
                    using var response = await client.SendAsync(request);
                    return response.StatusCode;
                }));
            Assert.All(responses, status => Assert.Equal(HttpStatusCode.OK, status));

            // The worker's own logs happened while the requests were in flight: its scope
            // carries identity but never a request CorrelationId, and the startup scope is
            // already released before the pipeline serves requests.
            using var workerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!capture.Logs.Any(log => log.Message.Contains("StructaDoc parse worker started")))
            {
                await Task.Delay(20, workerTimeout.Token);
            }

            var identityOnlyScopes = capture.Scopes
                .Where(scope =>
                    scope.Field(ServiceLogFieldNames.ServiceName) == logContext.ServiceName &&
                    scope.Field(ServiceLogFieldNames.InstanceId) == logContext.InstanceId &&
                    scope.Field(ServiceLogFieldNames.ServiceVersion) == logContext.ServiceVersion &&
                    scope.Field(ServiceLogFieldNames.CorrelationId) is null)
                .ToList();
            // At least the startup scope (released) and the worker scope (alive until shutdown).
            Assert.True(identityOnlyScopes.Count >= 2);
            Assert.Contains(identityOnlyScopes, scope => scope.Disposed);

            foreach (var id in new[] { "log-iso-a", "log-iso-b" })
            {
                var requestScope = Assert.Single(
                    capture.Scopes,
                    scope => scope.Field(ServiceLogFieldNames.CorrelationId) == id);
                Assert.True(requestScope.Disposed);
                Assert.Equal(
                    logContext.InstanceId,
                    requestScope.Field(ServiceLogFieldNames.InstanceId));
            }
        }
        finally
        {
            factory.Dispose();
        }

        // Bounded shutdown: after dispose every captured scope (worker included) is released.
        using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (capture.Scopes.Any(scope => !scope.Disposed))
        {
            await Task.Delay(20, shutdownTimeout.Token);
        }
    }

    [Fact]
    public async Task Shutdown_StaysBounded_WhenLokiIsUnreachable()
    {
        // Port 1 on loopback is closed: every delivery attempt fails fast, so shutdown must
        // stay inside the library's bounded flush/drain instead of hanging on the network.
        var factory = CreateFactory(
            settings: new Dictionary<string, string?> { ["Loki:Uri"] = "http://127.0.0.1:1" });
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        var stopwatch = Stopwatch.StartNew();
        factory.Dispose();
        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds < 15_000,
            $"host shutdown took {stopwatch.ElapsedMilliseconds}ms with an unreachable Loki");
    }

    [Fact]
    public void LegacySerilogConfigurationAssemblies_AreNoLongerReferenced()
    {
        // The removed hand-written wiring must leave no trace in the resolved dependency graph:
        // the legacy Serilog configuration/host-integration assemblies are gone. Serilog core,
        // Serilog.Extensions.Hosting, Serilog.Sinks.Console, and Serilog.Sinks.Grafana.Loki
        // remain only as transitive dependencies owned by the ServiceMantle.Logging package
        // itself, not as Doctheca.Consul direct references.
        var depsFile = Path.Combine(
            AppContext.BaseDirectory, "Doctheca.Tests.deps.json");
        Assert.True(File.Exists(depsFile), $"missing {depsFile}");
        var deps = File.ReadAllText(depsFile);

        foreach (var removed in new[]
                 {
                     "Serilog.AspNetCore",
                     "Serilog.Settings.Configuration",
                     "Serilog.Enrichers.Environment",
                     "Serilog.Enrichers.Thread"
                 })
        {
            Assert.DoesNotContain($"\"{removed}/", deps);
            Assert.DoesNotContain($"\"{removed}\"", deps);
        }

        // The library pipeline is present and pinned to the explicit pre-release.
        Assert.Contains("\"ServiceMantle.Logging/0.2.1-rc.1\"", deps);
    }

    private static async Task<WebApplication> StartFakeLokiAsync(Func<HttpContext, Task> onPush)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost("/loki/api/v1/push", onPush);
        await app.StartAsync();
        return app;
    }

    private static string ServerAddress(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();

    /// <summary>
    /// Emits the probe events from inside a real request (the search endpoint resolves
    /// ISearchDomainService → ISearchIndexService during endpoint execution, i.e. inside the
    /// correlation scope). Every canary is a synthetic, neutral value invented for this test:
    /// the denied-field canaries deliberately carry NO secret-looking name or text, so their
    /// absence from both sinks can only be explained by the sanitizer rejecting the structured
    /// FIELD NAME ({Password}, {Token}, …), not by any free-text secret pattern in the value.
    /// </summary>
    private sealed class LoggingProbeSearchIndex(ILoggerFactory loggerFactory) : ISearchIndexService
    {
        // Neutral values fed to the six denied structured field names below.
        public const string DeniedFieldCanary1 = "synthetic-denied-field-canary-1"; // {Password}
        public const string DeniedFieldCanary2 = "synthetic-denied-field-canary-2"; // {Token}
        public const string DeniedFieldCanary3 = "synthetic-denied-field-canary-3"; // {ApiKey}
        public const string DeniedFieldCanary4 = "synthetic-denied-field-canary-4"; // {Authorization}
        public const string DeniedFieldCanary5 = "synthetic-denied-field-canary-5"; // {Cookie}
        public const string DeniedFieldCanary6 = "synthetic-denied-field-canary-6"; // {ConnectionString}
        public const string ExceptionMessageCanary = "synthetic-exception-message-canary";
        public const string ExceptionDataCanary = "synthetic-exception-data-canary";
        public const string FreeTextCanary = "synthetic-freetext-canary";
        public const string NormalFieldMarker = "probe-normal-field-value";

        public Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)>
            ExactSearchAsync(
                string query,
                bool phrase,
                SearchFilterModel? filter,
                int pageSize,
                string? pageToken)
        {
            var logger = loggerFactory.CreateLogger("Doctheca.LoggingProbe");
            logger.LogInformation(
                "probe.application {ParseId} {NormalField}",
                Guid.NewGuid(),
                NormalFieldMarker);
            logger.LogInformation(
                "probe.secret {Password} {Token} {ApiKey} {Authorization} {Cookie} {ConnectionString}",
                DeniedFieldCanary1,
                DeniedFieldCanary2,
                DeniedFieldCanary3,
                DeniedFieldCanary4,
                DeniedFieldCanary5,
                DeniedFieldCanary6);
            logger.LogInformation(
                "probe.freetext Password={FreeTextSecret}", FreeTextCanary);
            var exception = new InvalidOperationException(ExceptionMessageCanary);
            exception.Data["detail"] = ExceptionDataCanary;
            logger.LogError(exception, "probe.exception");
            loggerFactory.CreateLogger("Microsoft.AspNetCore.Hosting")
                .LogInformation("probe.aspnet.information");
            loggerFactory.CreateLogger("Microsoft.AspNetCore.Hosting")
                .LogWarning("probe.aspnet.warning");
            loggerFactory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command")
                .LogInformation("probe.ef.information");
            loggerFactory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command")
                .LogWarning("probe.ef.warning");

            return Task.FromResult(
                (new List<SearchResultModel>(), 0, (string?)null));
        }

        public Task EnsureIndexAsync() => Task.CompletedTask;

        public Task IndexParseBlocksAsync(
            Guid parseId,
            Guid documentFileId,
            string fileName,
            string? subject,
            string? grade,
            string? year) => Task.CompletedTask;

        public Task DeleteParseIndexAsync(Guid parseId) => Task.CompletedTask;

        public Task DeleteDocumentFileIndexAsync(Guid documentFileId) => Task.CompletedTask;

        public Task UpdateDocumentFileMetadataAsync(
            Guid documentFileId,
            string? subject,
            string? grade,
            string? year) => Task.CompletedTask;
    }
}
