using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Doctheca.Host;
using Doctheca.Tests.Integration;
using Xunit;

namespace Doctheca.Tests.Hosting;

/// <summary>
/// Per-request correlation semantics of the Doctheca ServiceMantle wiring, exercised on an
/// isolated TestServer host built through the same foundation extension
/// (<c>AddDocthecaServiceMantleFoundation</c>) and the same pipeline entry
/// (<c>UseServiceMantleCorrelationId</c>) as the real Program.cs, with test-only endpoints for
/// the paths production cannot trigger on demand: a downstream response-header overwrite
/// converges to the resolved single value; exceptions and cancellation propagate untouched with
/// the request scope released; the rejected raw input is never logged; the original request
/// headers are never rewritten; the HttpContext accessor, the log scope, and the response
/// header always point at the same value; and this unselected raw HttpClient does not propagate the
/// correlation id (W3C trace propagation is a separate protocol and out of scope).
/// </summary>
public sealed partial class CorrelationPipelineSemanticsTests : IAsyncLifetime
{
    private const string CorrelationHeaderName = "x-correlation-id";
    private const string ValidValue = "semantics-correlation-0123456789";
    private const string CorrelationLoggerCategory = "ServiceMantle.Http.CorrelationId";

    private readonly RequestScopeCapture _capture = new();
    private readonly CapturingHandler _outbound = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex GeneratedIdPattern();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders().AddProvider(_capture);

        // The exact foundation entry point the real Program.cs uses.
        builder.Services.AddDocthecaServiceMantleFoundation();
        builder.Services.AddSingleton(new HttpClient(_outbound, disposeHandler: false));

        _app = builder.Build();

        // The exact pipeline entry point the real Program.cs uses, as the first middleware.
        _app.UseServiceMantleCorrelationId();

        _app.MapGet("/echo-raw", context =>
            context.Response.WriteAsync(
                string.Join("|", context.Request.Headers[CorrelationHeaderName].ToArray())));
        _app.MapGet("/accessor", context =>
            context.Response.WriteAsync(context.GetServiceMantleCorrelationId() ?? string.Empty));
        _app.MapGet("/overwrite", context =>
        {
            // A downstream component attempts to replace the correlation header (single and
            // repeated values): the middleware's OnStarting callback must converge the header
            // back to the one resolved value.
            context.Response.Headers[CorrelationHeaderName] = "downstream-attempt-123";
            context.Response.Headers.Append(CorrelationHeaderName, "second-downstream-attempt");
            return context.Response.WriteAsync("overwritten");
        });
        _app.MapGet("/throw", (Func<IResult>)(() => throw new InvalidOperationException("synthetic-boom")));
        _app.MapGet("/cancel", async context =>
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted));
        _app.MapGet("/outbound", async context =>
        {
            var httpClient = context.RequestServices.GetRequiredService<HttpClient>();
            using var downstream = await httpClient.GetAsync("http://downstream.test/api");
            await context.Response.WriteAsync("called-downstream");
        });
        _app.MapPost("/post-echo", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            return Results.Ok(new { length = body.Length });
        });

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _outbound.Dispose();
    }

    [Fact]
    public async Task DownstreamHeaderWrite_ConvergesToResolvedSingleValue()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/overwrite");
        request.Headers.Add(CorrelationHeaderName, ValidValue);

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Exactly one value, and it is the middleware-resolved one — not what downstream wrote.
        Assert.Equal(ValidValue, response.Headers.GetValues(CorrelationHeaderName).Single());
    }

    [Fact]
    public async Task Exception_Propagates_ScopeReleased_RawInputNotLogged()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/throw");
        request.Headers.TryAddWithoutValidation(CorrelationHeaderName, "raw-canary,bad value");

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => _client.SendAsync(request));

        // The original exception continues to propagate through the middleware.
        Assert.Contains("synthetic-boom", UnwrapMessage(exception));

        await WaitUntilAsync(() => _capture.Scopes.Count > 0 && _capture.Scopes.All(s => s.Disposed));

        // The resolved value is generated; the rejected raw input never reaches the logs and
        // the middleware itself logs nothing.
        Assert.DoesNotContain(_capture.Logs, log => log.Message.Contains("raw-canary"));
        Assert.DoesNotContain(_capture.Logs, log => log.Category == CorrelationLoggerCategory);
    }

    [Fact]
    public async Task Cancellation_Propagates_ScopeReleased()
    {
        using var cts = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/cancel");
        request.Headers.Add(CorrelationHeaderName, ValidValue);

        var send = _client.SendAsync(request, cts.Token);
        // Give the server endpoint time to enter its cancellable wait, then cancel.
        await Task.Delay(300);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);

        await WaitUntilAsync(() => _capture.Scopes.Count > 0 && _capture.Scopes.All(s => s.Disposed));
        Assert.Contains(
            _capture.Scopes,
            scope => scope.Field("CorrelationId") == ValidValue && scope.Disposed);
    }

    [Fact]
    public async Task OriginalRequestHeader_IsNeverRewritten()
    {
        // A rejected (comma-joined) value: the response header carries the generated id while
        // the original request header stays exactly what the caller sent.
        using var rejected = new HttpRequestMessage(HttpMethod.Get, "/echo-raw");
        rejected.Headers.TryAddWithoutValidation(CorrelationHeaderName, "a,b");

        using var rejectedResponse = await _client.SendAsync(rejected);
        Assert.Equal("a,b", await rejectedResponse.Content.ReadAsStringAsync());
        Assert.Matches(
            GeneratedIdPattern(),
            rejectedResponse.Headers.GetValues(CorrelationHeaderName).Single());

        // Repeated values: both stay on the request; the resolved response value is generated.
        using var repeated = new HttpRequestMessage(HttpMethod.Get, "/echo-raw");
        repeated.Headers.TryAddWithoutValidation(CorrelationHeaderName, "first-valid-id");
        repeated.Headers.TryAddWithoutValidation(CorrelationHeaderName, "second-valid-id");

        using var repeatedResponse = await _client.SendAsync(repeated);
        Assert.Equal(
            "first-valid-id|second-valid-id",
            await repeatedResponse.Content.ReadAsStringAsync());
        Assert.Matches(
            GeneratedIdPattern(),
            repeatedResponse.Headers.GetValues(CorrelationHeaderName).Single());
    }

    [Fact]
    public async Task Accessor_Scope_AndResponseHeader_PointAtTheSameValue()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/accessor");
        request.Headers.Add(CorrelationHeaderName, ValidValue);

        using var response = await _client.SendAsync(request);

        var header = response.Headers.GetValues(CorrelationHeaderName).Single();
        var accessor = await response.Content.ReadAsStringAsync();
        Assert.Equal(ValidValue, header);
        Assert.Equal(header, accessor);

        var scope = _capture.Scopes.Single(
            captured => captured.Field("CorrelationId") == accessor);
        Assert.True(scope.Disposed);
        // The same scope also carries the foundation identity fields.
        Assert.Equal("doctheca", scope.Field("ServiceName"));
        Assert.False(string.IsNullOrEmpty(scope.Field("InstanceId")));
        Assert.False(string.IsNullOrEmpty(scope.Field("ServiceVersion")));
    }

    [Fact]
    public async Task OutboundHttpClient_DoesNotPropagateCorrelationId()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/outbound");
        request.Headers.Add(CorrelationHeaderName, ValidValue);

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var outboundRequest = Assert.Single(_outbound.Requests);
        // The correlation id is a log-correlation value only: no custom outbound propagation.
        Assert.False(outboundRequest.Headers.Contains(CorrelationHeaderName));
        Assert.False(outboundRequest.Headers.Contains("X-Correlation-Id"));
    }

    [Fact]
    public async Task Instrumentation_CapturesNoCredentialsOrBodyTags()
    {
        // The foundation registers the library's default instrumentation only (no configure
        // callback, no enrichment), so spans must not carry request headers, credentials, or
        // body content. Collect every activity produced during one request that carries an
        // Authorization header and a body, and inspect the recorded tags.
        using var collector = new ActivityCollector();

        const string authCanary = "Bearer auth-canary-fictitious-value";
        const string bodyCanary = "body-canary-fictitious-value";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/post-echo");
        request.Headers.TryAddWithoutValidation("Authorization", authCanary);
        request.Headers.Add(CorrelationHeaderName, ValidValue);
        request.Content = new StringContent(bodyCanary);

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var serverActivity = await collector.WaitForActivityAsync(
            activity => activity.OperationName == "Microsoft.AspNetCore.Hosting.HttpRequestIn");
        Assert.NotNull(serverActivity);

        Assert.All(collector.Activities, activity =>
        {
            foreach (var tag in activity.Tags)
            {
                Assert.DoesNotContain("auth-canary", tag.Value ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain("body-canary", tag.Value ?? string.Empty, StringComparison.Ordinal);
                Assert.False(
                    tag.Key.StartsWith("http.request.header.", StringComparison.OrdinalIgnoreCase) ||
                    tag.Key.StartsWith("http.response.header.", StringComparison.OrdinalIgnoreCase),
                    $"Span tag {tag.Key} must not capture raw HTTP headers.");
            }
        });
    }

    private static string UnwrapMessage(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return exception.Message + "|" + current.Message;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The awaited pipeline state was not reached within 5 seconds.");
            }

            await Task.Delay(25);
        }
    }

    /// <summary>
    /// Records the outbound requests an endpoint makes without touching the network.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly object _gate = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Requests.Add(request);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("downstream-stub")
            });
        }
    }

    /// <summary>
    /// Collects every <see cref="Activity"/> started while the listener is alive, with full
    /// tag recording, so telemetry content can be inspected in-process.
    /// </summary>
    private sealed class ActivityCollector : IDisposable
    {
        private readonly ActivityListener _listener;

        public ActivityCollector()
        {
            Activities = new ConcurrentBag<Activity>();
            _listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => Activities.Add(activity)
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public ConcurrentBag<Activity> Activities { get; }

        public async Task<Activity?> WaitForActivityAsync(Func<Activity, bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var match = Activities.FirstOrDefault(predicate);
                if (match is not null)
                {
                    return match;
                }

                await Task.Delay(25);
            }

            return null;
        }

        public void Dispose() => _listener.Dispose();
    }
}
