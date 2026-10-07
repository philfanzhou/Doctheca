using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Doctheca.Tests.Integration;

[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed class ServiceMantleOutgoingCorrelationTests(PostgreSqlFixture database)
    : ServiceMantleIntegrationTestBase(database)
{
    [Fact]
    public async Task ProductionClient_UsesResolvedSlot_PreservesExplicitHeader_AndIsolatesPooledRequests()
    {
        var capture = new CaptureHandler();
        await using var factory = CreateFactory(configureTestServices: services =>
        {
            services.AddHttpClient("StructaDoc").ConfigurePrimaryHttpMessageHandler(() => capture);
            services.AddHttpClient("external").ConfigurePrimaryHttpMessageHandler(() => capture);
            services.AddSingleton<IAuthorizationMiddlewareResultHandler, OutgoingProbe>();
        }, settings: new Dictionary<string, string?> { ["StructaDoc:BaseUrl"] = "http://structadoc.test/" });
        using var client = factory.CreateClient();
        var tasks = Enumerable.Range(0, 16).Select(async index =>
        {
            var id = $"incoming-correlation-{index:D2}";
            using var request = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute + $"?probe={index}");
            request.Headers.Add(CorrelationHeaderName, id);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(id, response.Headers.GetValues(CorrelationHeaderName).Single());
            Assert.Equal(new[] { id }, capture.Headers[index.ToString()]);
            Assert.Empty(capture.Headers[$"external-{index}"]);
        });
        await Task.WhenAll(tasks);

        using var explicitRequest = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute + "?probe=explicit&explicit=true");
        explicitRequest.Headers.Add(CorrelationHeaderName, "incoming-explicit-probe");
        using var explicitResponse = await client.SendAsync(explicitRequest);
        Assert.Equal(HttpStatusCode.OK, explicitResponse.StatusCode);
        Assert.Equal(new[] { "outgoing-explicit-value" }, capture.Headers["explicit"]);

        using var invalidRequest = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute + "?probe=invalid");
        invalidRequest.Headers.TryAddWithoutValidation(CorrelationHeaderName, "rejected, input");
        using var invalidResponse = await client.SendAsync(invalidRequest);
        var resolved = invalidResponse.Headers.GetValues(CorrelationHeaderName).Single();
        Assert.Matches("^[0-9a-f]{32}$", resolved);
        Assert.Equal(new[] { resolved }, capture.Headers["invalid"]);

        using var background = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("StructaDoc");
        Assert.Equal(new Uri("http://structadoc.test/"), background.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(300), background.Timeout);
        using var backgroundResponse = await background.GetAsync("background");
        Assert.Empty(capture.Headers["background"]);
    }

    [Fact]
    public async Task ProductionClient_CallerCancellationAtCompletion_DisposesResponse_AndKeepsToken()
    {
        using var cancellation = new CancellationTokenSource();
        var capture = new CaptureHandler(cancellation);
        await using var factory = CreateFactory(configureTestServices: services =>
        {
            services.AddHttpClient("StructaDoc").ConfigurePrimaryHttpMessageHandler(() => capture);
            services.AddSingleton(cancellation);
            services.AddSingleton<IAuthorizationMiddlewareResultHandler, CancellationProbe>();
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute);
        request.Headers.Add(CorrelationHeaderName, "completion-cancel-probe");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("cancelled", await response.Content.ReadAsStringAsync());
        Assert.True(capture.Content!.Disposed);
        Assert.Equal(new[] { "completion-cancel-probe" }, capture.Headers["cancel"]);
        using var selected = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("StructaDoc");
        var before = capture.Headers.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selected.GetAsync("http://structadoc.test/entry", cancellation.Token));
        Assert.Equal(before, capture.Headers.Count);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => selected.GetAsync("http://structadoc.test/failure"));
        Assert.Equal("synthetic inner failure", failure.Message);
    }

    private sealed class OutgoingProbe : IAuthorizationMiddlewareResultHandler
    {
        public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            var probe = context.Request.Query["probe"].ToString();
            // Mutating the inbound header after middleware resolution must not replace its private slot.
            context.Request.Headers[CorrelationHeaderName] = "downstream-mutated-inbound";
            var factory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
            using var selected = factory.CreateClient("StructaDoc");
            using var outgoing = new HttpRequestMessage(HttpMethod.Get, "http://structadoc.test/" + probe);
            if (context.Request.Query.ContainsKey("explicit")) outgoing.Headers.Add(CorrelationHeaderName, "outgoing-explicit-value");
            using var response = await selected.SendAsync(outgoing, context.RequestAborted);
            using var external = factory.CreateClient("external");
            using var externalResponse = await external.GetAsync("http://external.test/external-" + probe, context.RequestAborted);
            await context.Response.WriteAsync("sent");
        }
    }

    private sealed class CancellationProbe : IAuthorizationMiddlewareResultHandler
    {
        public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            var cancellation = context.RequestServices.GetRequiredService<CancellationTokenSource>();
            using var selected = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("StructaDoc");
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selected.GetAsync("http://structadoc.test/cancel", cancellation.Token));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            await context.Response.WriteAsync("cancelled");
        }
    }

    private sealed class CaptureHandler(CancellationTokenSource? cancellation = null) : HttpMessageHandler
    {
        public ConcurrentDictionary<string, string[]> Headers { get; } = new();
        public TrackedContent? Content { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (request.RequestUri!.AbsolutePath == "/failure") throw new InvalidOperationException("synthetic inner failure");
            Headers[request.RequestUri!.AbsolutePath.TrimStart('/')] = request.Headers.TryGetValues(CorrelationHeaderName, out var values) ? values.ToArray() : [];
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Content = new TrackedContent() };
            cancellation?.Cancel();
            return response;
        }
    }

    private sealed class TrackedContent : StringContent
    {
        public TrackedContent() : base("synthetic") { }
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }
}
