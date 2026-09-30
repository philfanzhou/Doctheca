using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ServiceMantle.Web.Logging;
using Xunit;

namespace Doctheca.Tests.Integration;

/// <summary>
/// Per-request correlation behavior of the real host with the ServiceMantle middleware wired
/// as the first pipeline item: a single shape-valid inbound x-correlation-id is echoed
/// verbatim; missing, whitespace, overlong, illegal, comma-joined, or repeated inputs are
/// discarded whole and replaced by a generated 32-character lowercase hex id; the header is
/// carried by static files, the SPA fallback, anonymous auth, health, 401, and 403 responses
/// alike; concurrent request scopes never cross values; and for one and the same request the
/// response header and the request log scope carry the same id alongside the ServiceMantle
/// identity fields.
/// </summary>
[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed partial class ServiceMantleCorrelationTests : ServiceMantleIntegrationTestBase
{
    private const string ValidValue = "e2e-correlation-0123456789abcdef";

    public ServiceMantleCorrelationTests(PostgreSqlFixture database) : base(database)
    {
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex GeneratedIdPattern();

    [Fact]
    public async Task ValidHeader_IsEchoedVerbatim_OnEveryRouteKind()
    {
        var contentRoot = CreateTempContentRoot(withWwwrootStub: true);
        try
        {
            using var factory = CreateFactory(contentRoot: contentRoot);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add(CorrelationHeaderName, ValidValue);

            // Static asset.
            using var staticAsset = await client.GetAsync("/probe.txt");
            Assert.Equal(HttpStatusCode.OK, staticAsset.StatusCode);
            Assert.Equal(ValidValue, staticAsset.Headers.GetValues(CorrelationHeaderName).Single());

            // SPA fallback.
            using var spa = await client.GetAsync("/documents/library");
            Assert.Equal(HttpStatusCode.OK, spa.StatusCode);
            Assert.Equal(ValidValue, spa.Headers.GetValues(CorrelationHeaderName).Single());

            // Anonymous health.
            using var health = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal(ValidValue, health.Headers.GetValues(CorrelationHeaderName).Single());

            // The header is injected before authentication runs: even the 401 of an
            // unauthenticated admin API request carries the correlation id.
            using var api = await client.GetAsync(ProtectedApiRoute);
            Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
            Assert.Equal(ValidValue, api.Headers.GetValues(CorrelationHeaderName).Single());
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public async Task MissingHeader_GeneratesNewId()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Matches(GeneratedIdPattern(), health.Headers.GetValues(CorrelationHeaderName).Single());

        using var api = await client.GetAsync(ProtectedApiRoute);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Matches(GeneratedIdPattern(), api.Headers.GetValues(CorrelationHeaderName).Single());
    }

    [Theory]
    [InlineData(" ")]        // whitespace-only is rejected whole
    [InlineData("a b")]      // illegal character
    [InlineData("a,b")]      // comma-joined value
    [InlineData(".leading")] // first character is not alphanumeric
    [InlineData("id\u00e9")] // non-ASCII character
    public async Task RejectedValue_IsReplacedByGeneratedId(string value)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(CorrelationHeaderName, value);

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var resolved = response.Headers.GetValues(CorrelationHeaderName).Single();
        Assert.Matches(GeneratedIdPattern(), resolved);
        Assert.NotEqual(value, resolved);
    }

    [Fact]
    public async Task OverlongValue_IsReplacedByGeneratedId()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            CorrelationHeaderName,
            new string('a', 65)); // one character beyond the 64-character maximum

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches(GeneratedIdPattern(), response.Headers.GetValues(CorrelationHeaderName).Single());
    }

    [Fact]
    public async Task RepeatedHeader_IsDiscardedWhole()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(CorrelationHeaderName, ValidValue);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            CorrelationHeaderName, "another-valid-id-1234");

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var resolved = response.Headers.GetValues(CorrelationHeaderName).Single();
        Assert.Matches(GeneratedIdPattern(), resolved);
        Assert.NotEqual(ValidValue, resolved);
        Assert.NotEqual("another-valid-id-1234", resolved);
    }

    [Fact]
    public async Task ConcurrentRequests_KeepScopesSeparated()
    {
        var capture = new RequestScopeCapture();
        using var factory = CreateFactory(configureTestServices: services =>
        {
            services.RemoveAll<ILoggerFactory>();
            services.AddSingleton<ILoggerFactory>(
                _ => LoggerFactory.Create(logging => logging.AddProvider(capture)));
        });
        using var client = factory.CreateClient();

        const int requestCount = 16;
        var responses = await Task.WhenAll(Enumerable.Range(0, requestCount).Select(async index =>
        {
            var expected = $"conc-{index:D2}-abcdef";
            using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
            request.Headers.Add(CorrelationHeaderName, expected);
            using var response = await client.SendAsync(request);
            return (
                Expected: expected,
                Status: response.StatusCode,
                Resolved: response.Headers.GetValues(CorrelationHeaderName).Single());
        }));

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.Status);
            // Each request echoes exactly its own id: no cross-request value leakage.
            Assert.Equal(response.Expected, response.Resolved);
            // Exactly one request scope carries each id, and it is released after the request.
            var matchingScope = Assert.Single(
                capture.Scopes,
                scope => scope.Field(ServiceLogFieldNames.CorrelationId) == response.Expected);
            Assert.True(matchingScope.Disposed);
        }
    }

    [Fact]
    public async Task ResponseHeader_AndLogScope_CarrySameCorrelationId()
    {
        var capture = new RequestScopeCapture();
        using var factory = CreateFactory(configureTestServices: services =>
        {
            // Swap the ServiceMantle Serilog logger factory for a plain per-factory one (see
            // RequestScopeCapture): the request scope state is recorded directly instead of
            // going through the real Console/Loki pipeline. Non-request identity scopes
            // (startup, background worker) are opened explicitly through ServiceLogContext
            // since the logging migration; actual Console/Loki delivery is asserted in
            // ServiceMantleLoggingTests.
            services.RemoveAll<ILoggerFactory>();
            services.AddSingleton<ILoggerFactory>(
                _ => LoggerFactory.Create(logging => logging.AddProvider(capture)));
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(CorrelationHeaderName, ValidValue);

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var correlationId = response.Headers.GetValues(CorrelationHeaderName).Single();
        Assert.Equal(ValidValue, correlationId);

        // The scope opened by the middleware during this request carries the same id, together
        // with the identity fields of the host's ServiceLogContext.
        var scope = capture.Scopes.Single(
            captured => captured.Field(ServiceLogFieldNames.CorrelationId) == correlationId);
        Assert.True(scope.Disposed);
        var logContext = factory.Services.GetRequiredService<ServiceLogContext>();
        Assert.Equal(logContext.ServiceName, scope.Field(ServiceLogFieldNames.ServiceName));
        Assert.Equal(logContext.ServiceVersion, scope.Field(ServiceLogFieldNames.ServiceVersion));
        Assert.Equal(logContext.InstanceId, scope.Field(ServiceLogFieldNames.InstanceId));
    }
}
