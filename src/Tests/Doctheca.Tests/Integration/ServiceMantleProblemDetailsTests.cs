using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Doctheca.Common.Oss;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Host.Authentication;
using Moq;
using Xunit;

namespace Doctheca.Tests.Integration;

/// <summary>
/// ServiceMantle safe Problem Details boundary on the real host: unhandled exceptions on the
/// marked JSON admin endpoints become a fixed, safe <c>500 application/problem+json</c> whose
/// field set is exactly <c>type,title,status,correlationId,errorCode</c> — never exception
/// message, inner exception, <c>Data</c>, type names, or stack — identical in Development and
/// Production, sharing one correlation id across response header, problem body, and log scope
/// while Console/Loki sinks stay free of exception content; handler-thrown
/// <see cref="BadHttpRequestException"/> keeps its framework status through the ordered
/// candidates (413 → 415 → unconditional 400); minimal-API binding failures stay empty-body
/// 400/415 and never become problems; the multipart body-length limit (an
/// <c>InvalidDataException</c>, not a <see cref="BadHttpRequestException"/>) normalizes to the
/// generic 500 exactly like today's unhandled status; business results, 401/403 challenges,
/// and the six single-value security headers are preserved; caller cancellation propagates
/// without a problem body while an independent <see cref="OperationCanceledException"/>
/// normalizes to 500; once a marked response has started the library swallows the failure and
/// keeps status and sent bytes; and unmarked routes (health/static/SPA/export/image proxy)
/// never enter the branch — unmarked failures surface unconverted with their rendering
/// contracts intact.
/// </summary>
[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed class ServiceMantleProblemDetailsTests : ServiceMantleIntegrationTestBase
{
    // Synthetic exception-content canaries: they must never appear in any response body,
    // Console line, or Loki batch.
    private const string CanaryMessage = "synthetic-problem-message-canary";
    private const string CanaryData = "synthetic-problem-data-canary";
    private const string InnerCanary = "synthetic-problem-inner-canary";
    private const string StreamCanary = "synthetic-problem-stream-canary";

    // The six immutable security-header values (verbatim from the library baseline): problem
    // responses must carry them too, because the security-header middleware runs outside the
    // Problem Details branch and writes via OnStarting.
    private const string CspValue =
        "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    private static readonly (string Name, string Value)[] Baseline =
    [
        ("Cache-Control", "no-store"),
        ("Pragma", "no-cache"),
        ("X-Content-Type-Options", "nosniff"),
        ("X-Frame-Options", "DENY"),
        ("Referrer-Policy", "no-referrer"),
        ("Content-Security-Policy", CspValue),
    ];

    private static readonly string[] ExactProblemFields =
        ["type", "title", "status", "correlationId", "errorCode"];

    private const string ListRoute = "/admin/document-files";

    public ServiceMantleProblemDetailsTests(PostgreSqlFixture database) : base(database)
    {
    }

    // ── unhandled exception: the fixed safe 500, identical across environments ──

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task UnhandledException_OnMarkedEndpoint_ReturnsSameSafeProblem_InBothEnvironments(
        string environment)
    {
        // The fixture pins the environment to Testing; overriding the host-configuration
        // environment key (WebHostDefaults.EnvironmentKey) rebuilds the same Program.cs under
        // the requested environment. The safe problem must be byte-identical in both.
        using var factory = CreateFactory(
            configureTestServices: services => UseProbeFileRepository(
                services, _ => throw CanaryException()),
            settings: new Dictionary<string, string?> { ["environment"] = environment });
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, ListRoute, "admin");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(body).RootElement;

        // The field set is exactly type/title/status/correlationId/errorCode — no detail,
        // no traceId, no extension members.
        var fields = root.EnumerateObject().Select(property => property.Name).ToList();
        Assert.Equal(ExactProblemFields.Length, fields.Count);
        foreach (var expected in ExactProblemFields)
        {
            Assert.Contains(expected, fields);
        }

        Assert.Equal(
            "urn:servicemantle:error:http.internal_server_error",
            root.GetProperty("type").GetString());
        Assert.Equal("An unexpected error occurred.", root.GetProperty("title").GetString());
        Assert.Equal(500, root.GetProperty("status").GetInt32());
        Assert.Equal("http.internal_server_error", root.GetProperty("errorCode").GetString());

        // One correlation id, shared by the response header and the body.
        var bodyCorrelation = root.GetProperty("correlationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(bodyCorrelation));
        Assert.Equal(bodyCorrelation, AllValues(response, CorrelationHeaderName).Single());

        // No exception content of any kind.
        Assert.DoesNotContain(CanaryMessage, body);
        Assert.DoesNotContain(CanaryData, body);
        Assert.DoesNotContain(InnerCanary, body);
        Assert.DoesNotContain("InvalidOperationException", body);
        Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);

        // The six security headers still cover the problem response, each single-valued.
        AssertBaseline(response);
    }

    [Fact]
    public async Task ProblemResponse_SharesOneCorrelationId_AndBothSinksStaySafe()
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
                configureTestServices: services => UseProbeFileRepository(
                    services, _ => throw CanaryException()),
                settings: new Dictionary<string, string?> { ["Loki:Uri"] = address });
            using var client = factory.CreateClient();

            using var request = new HttpRequestMessage(HttpMethod.Get, ListRoute);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", CreateToken("admin"));
            // A contract-valid caller-supplied id, so all three legs are directly comparable.
            request.Headers.Add(CorrelationHeaderName, "problem-corr-0123456789");
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            var bodyCorrelation =
                JsonDocument.Parse(body).RootElement.GetProperty("correlationId").GetString();
            var headerCorrelation = AllValues(response, CorrelationHeaderName).Single();
            Assert.Equal("problem-corr-0123456789", headerCorrelation);
            Assert.Equal(headerCorrelation, bodyCorrelation);

            // Loki batching is asynchronous; wait for the problem event, not just startup.
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!batches.Any(batch => batch.Contains("http.internal_server_error")))
            {
                await Task.Delay(20, timeout.Token);
            }

            var remote = string.Join('\n', batches
                .SelectMany(batch => JsonDocument.Parse(batch).RootElement
                    .GetProperty("streams").EnumerateArray()
                    .SelectMany(stream => stream.GetProperty("values").EnumerateArray()
                        .Select(value => value[1].GetString()!))));
            var console = output.ToString();

            foreach (var sink in new[] { console, remote })
            {
                // The library log carries the fixed safe code and the same correlation id —
                // the third leg of the header/body/log-scope equality.
                Assert.Contains("http.internal_server_error", sink);
                var problemLine = sink
                    .Split('\n')
                    .First(line => line.Contains("http.internal_server_error"));
                Assert.Contains(headerCorrelation, problemLine);

                // Exception message/Data/inner content never reaches either sink.
                Assert.DoesNotContain(CanaryMessage, sink);
                Assert.DoesNotContain(CanaryData, sink);
                Assert.DoesNotContain(InnerCanary, sink);
            }
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    // ── BadHttpRequestException: ordered candidates preserve the framework statuses ──

    [Theory]
    [InlineData(
        StatusCodes.Status413PayloadTooLarge,
        HttpStatusCode.RequestEntityTooLarge,
        "http.payload_too_large",
        "The request payload is too large.")]
    [InlineData(
        StatusCodes.Status415UnsupportedMediaType,
        HttpStatusCode.UnsupportedMediaType,
        "http.unsupported_media_type",
        "The request media type is not supported.")]
    [InlineData(
        StatusCodes.Status400BadRequest,
        HttpStatusCode.BadRequest,
        "http.request_invalid",
        "The request could not be processed.")]
    public async Task HandlerThrownBadHttpRequestException_PreservesStatus_WithFixedSafeFields(
        int thrownStatus, HttpStatusCode expectedStatus, string expectedCode, string expectedTitle)
    {
        using var factory = CreateFactory(configureTestServices: services =>
            UseProbeFileRepository(
                services, _ => throw new BadHttpRequestException(CanaryMessage, thrownStatus)));
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, ListRoute, "admin");

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(expectedCode, root.GetProperty("errorCode").GetString());
        Assert.Equal(expectedTitle, root.GetProperty("title").GetString());
        Assert.Equal((int)expectedStatus, root.GetProperty("status").GetInt32());
        // No input echo: the thrown message stays out of the response.
        Assert.DoesNotContain(CanaryMessage, body);
        AssertBaseline(response);
    }

    // ── binding failures never reach the branch: no problem responses ──

    [Fact]
    public async Task BindingFailures_OnMarkedLogin_NeverProduceProblemResponses()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Malformed JSON body: handled inside the minimal-API endpoint factory of the marked
        // (AllowAnonymous) login endpoint, so the branch is never entered: 400 + empty body,
        // identical before and after this slice.
        using (var malformed = await client.PostAsync(
                   "/admin/auth/login", new StringContent("{ not json", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            Assert.Equal(0, (await malformed.Content.ReadAsStringAsync()).Length);
            Assert.NotEqual("application/problem+json", malformed.Content.Headers.ContentType?.MediaType);
            AssertBaseline(malformed);
        }

        // Content-Type mismatching the JSON binding: .NET 10 routing selects the framework's
        // synthesized "415 HTTP Unsupported Media Type" endpoint, which carries neither the
        // marker nor AllowAnonymous. Authenticated callers therefore get the pre-existing
        // 415 + empty body, and anonymous callers hit the host's authorization FallbackPolicy
        // and get the pre-existing 401 challenge. Both outcomes were verified identical on
        // the pre-slice baseline (main @0bdb020); neither ever becomes a problem response.
        using (var authedWrongType = await SendPostAsync(
                   client, "/admin/auth/login",
                   new StringContent("username=x", Encoding.UTF8, "text/plain"), "admin"))
        {
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, authedWrongType.StatusCode);
            Assert.Equal(0, (await authedWrongType.Content.ReadAsStringAsync()).Length);
            Assert.NotEqual(
                "application/problem+json", authedWrongType.Content.Headers.ContentType?.MediaType);
        }

        using (var anonWrongType = await client.PostAsync(
                   "/admin/auth/login", new StringContent("username=x", Encoding.UTF8, "text/plain")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonWrongType.StatusCode);
            Assert.Equal(0, (await anonWrongType.Content.ReadAsStringAsync()).Length);
            Assert.NotEqual(
                "application/problem+json", anonWrongType.Content.Headers.ContentType?.MediaType);
            Assert.Equal("Bearer", string.Join(",", AllValues(anonWrongType, "WWW-Authenticate")));
        }
    }

    // ── multipart body-length limit: InvalidDataException stays a generic 500 ──

    [Fact]
    public async Task OversizedMultipartForm_OnMarkedUpload_NormalizesToGenericProblem500()
    {
        // ReadFormAsync over the multipart body-length limit throws System.IO.InvalidDataException
        // (NOT BadHttpRequestException), so it is deliberately unmapped and normalizes to the
        // generic 500 — the same unhandled status as today, only the body shape changes.
        using var factory = CreateFactory(
            configureTestServices: services =>
                services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 1024),
            settings: new Dictionary<string, string?>
            {
                // Synthetic values so the endpoint reaches ReadFormAsync instead of the
                // not-configured 503; the StructaDoc client stays a mock and is never called.
                ["StructaDoc:BaseUrl"] = "http://127.0.0.1:1",
                ["StructaDoc:ApiKey"] = "sd1.synthetic-problem-test-key",
            });
        using var client = factory.CreateClient();

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(new byte[4096]);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(fileContent, "file", "problem-probe.pdf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/document-files/upload")
        {
            Content = form
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin"));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("http.internal_server_error", root.GetProperty("errorCode").GetString());
        AssertBaseline(response);
    }

    // ── business results and auth challenges are never wrapped ──

    [Fact]
    public async Task BusinessResults_AndAuthChallenges_OnMarkedEndpoints_AreNeverWrapped()
    {
        // First login reaches the identity double and is rejected (401 business JSON); the
        // second reports the identity service unavailable (502 business JSON).
        var calls = 0;
        var identity = new Mock<IIdentityAuthenticationService>();
        identity
            .Setup(service => service.PasswordGrantAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref calls) == 1
                ? new IdentityTokenExchangeResult(IdentityExchangeStatus.Rejected)
                : new IdentityTokenExchangeResult(IdentityExchangeStatus.Unavailable));

        using var factory = CreateFactory(configureTestServices: services =>
        {
            services.RemoveAll<IIdentityAuthenticationService>();
            services.AddSingleton(identity.Object);
        });
        using var client = factory.CreateClient();

        // login: validation 400, rejected 401, identity-unavailable 502 — all business JSON.
        await AssertBusinessJsonAsync(
            await client.PostAsJsonAsync("/admin/auth/login", new { username = "", password = "" }),
            HttpStatusCode.BadRequest, "Username and password are required.");
        await AssertBusinessJsonAsync(
            await client.PostAsJsonAsync(
                "/admin/auth/login", new { username = "someone", password = "synthetic-wrong" }),
            HttpStatusCode.Unauthorized, "Invalid username or password.");
        await AssertBusinessJsonAsync(
            await client.PostAsJsonAsync(
                "/admin/auth/login", new { username = "someone", password = "synthetic-wrong" }),
            HttpStatusCode.BadGateway, "Identity service is unavailable.");

        // refresh without a cookie, logout, and the session endpoint keep their contracts.
        await AssertBusinessJsonAsync(
            await client.PostAsync("/admin/auth/refresh", content: null),
            HttpStatusCode.Unauthorized, "Authentication is required.");
        await AssertBusinessJsonAsync(
            await client.PostAsync("/admin/auth/logout", content: null),
            HttpStatusCode.OK, "\"success\":true");

        // 401/403 challenges on a marked endpoint stay challenges, not problems.
        using (var anon = await client.GetAsync("/admin/auth/session"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
            Assert.NotEqual(
                "application/problem+json", anon.Content.Headers.ContentType?.MediaType);
            // The 401 stays a JSON challenge, not the SPA page.
            Assert.NotEqual("text/html", anon.Content.Headers.ContentType?.MediaType);
            AssertBaseline(anon);
        }

        using (var forbidden = await SendAsync(client, "/admin/auth/session", "user"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.NotEqual(
                "application/problem+json", forbidden.Content.Headers.ContentType?.MediaType);
            AssertBaseline(forbidden);
        }

        await AssertBusinessJsonAsync(
            await SendAsync(client, "/admin/auth/session", "admin"),
            HttpStatusCode.OK, "\"success\":true");

        // A marked business 400 with the DOCTHECA_* code contract.
        await AssertBusinessJsonAsync(
            await SendAsync(client, "/admin/documents/search?query=", "admin"),
            HttpStatusCode.BadRequest, "DOCTHECA_QUERY_REQUIRED");

        // Upload with StructaDoc not configured: the existing business 503.
        await AssertBusinessJsonAsync(
            await SendMultipartAsync(client, "/admin/document-files/upload", "admin"),
            HttpStatusCode.ServiceUnavailable, "DOCTHECA_STRUCTADOC_NOT_CONFIGURED");

        // A marked 422 business result: parse trigger on a file with an in-progress parse.
        var (fileId, parseId) = await SeedFileWithParsingParseAsync(factory);
        try
        {
            using var parseRequest = new HttpRequestMessage(
                HttpMethod.Post, $"/admin/document-files/{fileId}/parse");
            parseRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", CreateToken("admin"));
            await AssertBusinessJsonAsync(
                await client.SendAsync(parseRequest),
                HttpStatusCode.UnprocessableEntity, "DOCTHECA_PARSE_IN_PROGRESS");
        }
        finally
        {
            await DeleteSeededAsync(factory, fileId, parseId, imageId: null);
        }

        // The marked success response keeps its business JSON shape.
        await AssertBusinessJsonAsync(
            await SendAsync(client, ListRoute, "admin"),
            HttpStatusCode.OK, "\"success\":true");
    }

    // ── cancellation semantics ──

    [Fact]
    public async Task CallerCancellation_OnMarkedEndpoint_Propagates_WithoutProblemBody()
    {
        using var factory = CreateFactory(configureTestServices: services =>
            UseProbeFileRepository(services, async accessor =>
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan, accessor.HttpContext!.RequestAborted);
                return (new List<DocumentFileModel>(), 0);
            }));
        using var client = factory.CreateClient();
        using var cts = new CancellationTokenSource();

        using var request = new HttpRequestMessage(HttpMethod.Get, ListRoute);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin"));
        var send = client.SendAsync(request, cts.Token);
        // Give the handler time to enter its cancellable wait, then cancel.
        await Task.Delay(300);
        await cts.CancelAsync();

        // The cancellation surfaces to the caller; no problem response was produced instead.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
    }

    [Fact]
    public async Task IndependentOperationCanceledException_NormalizesToProblem500()
    {
        using var factory = CreateFactory(configureTestServices: services =>
            UseProbeFileRepository(services, _ => throw new OperationCanceledException()));
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, ListRoute, "admin");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("http.internal_server_error", root.GetProperty("errorCode").GetString());
    }

    // ── response already started on a marked endpoint: the library non-guarantee ──

    [Fact]
    public async Task ResponseAlreadyStarted_OnMarkedEndpoint_KeepsStatusAndSentBytes()
    {
        // Documented library boundary: once the response has started, the exception is
        // swallowed with a safe log, and the already-sent status and bytes are preserved
        // (the marked JSON path completes normally, so TestServer keeps the flushed bytes).
        using var factory = CreateFactory(configureTestServices: services =>
            UseProbeFileRepository(services, async accessor =>
            {
                var response = accessor.HttpContext!.Response;
                await response.Body.WriteAsync(Encoding.UTF8.GetBytes("partial-body"));
                await response.Body.FlushAsync();
                throw CanaryException();
            }));
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, ListRoute, "admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("partial-body", body);
        Assert.DoesNotContain(CanaryMessage, body);
        AssertBaseline(response);
    }

    // ── unmarked routes never enter the branch ──

    [Fact]
    public async Task UnmarkedHealthStaticAndSpa_NeverEnterTheBranch()
    {
        var contentRoot = CreateTempContentRoot(withWwwrootStub: true);
        try
        {
            using var factory = CreateFactory(contentRoot: contentRoot);
            using var client = factory.CreateClient();

            using (var health = await client.GetAsync("/health"))
            {
                Assert.Equal(HttpStatusCode.OK, health.StatusCode);
                Assert.Equal("application/json", health.Content.Headers.ContentType?.MediaType);
                Assert.Contains("\"status\"", await health.Content.ReadAsStringAsync());
                AssertNoBaseline(health);
            }

            using (var staticAsset = await client.GetAsync("/probe.txt"))
            {
                Assert.Equal(HttpStatusCode.OK, staticAsset.StatusCode);
                Assert.Equal("doctheca-static-probe", await staticAsset.Content.ReadAsStringAsync());
                AssertNoBaseline(staticAsset);
            }

            using (var spa = await client.GetAsync("/documents/library"))
            {
                Assert.Equal(HttpStatusCode.OK, spa.StatusCode);
                Assert.Equal("text/html", spa.Content.Headers.ContentType?.MediaType);
                Assert.Contains(
                    "doctheca-stub-index-marker", await spa.Content.ReadAsStringAsync());
                AssertNoBaseline(spa);
            }
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UnmarkedExportHtml_RendersUnchanged_WithoutProblemSemantics()
    {
        using var factory = CreateFactory();
        var (fileId, parseId, _) = await SeedParsedDocumentAsync(factory, withImage: false);
        try
        {
            using var client = factory.CreateClient();
            using var html = await SendAsync(
                client, $"/admin/document-files/{fileId}/export/html", "admin");

            Assert.Equal(HttpStatusCode.OK, html.StatusCode);
            Assert.Equal("text/html", html.Content.Headers.ContentType?.MediaType);
            var body = await html.Content.ReadAsStringAsync();
            Assert.Contains("problem-details-probe", body);
            Assert.DoesNotContain("application/problem+json", body);
            AssertNoBaseline(html);
        }
        finally
        {
            await DeleteSeededAsync(factory, fileId, parseId, imageId: null);
        }
    }

    [Fact]
    public async Task UnmarkedException_BeforeResponseStart_SurfacesUnconverted()
    {
        // The unmarked image content proxy with a failing legacy OSS download: the failure
        // must surface to the caller exactly as today — the branch is never entered, so no
        // problem response is produced.
        var oss = new Mock<IOssService>();
        oss.Setup(service => service.DownloadAsync(It.IsAny<string>()))
            .ThrowsAsync(CanaryException());

        using var factory = CreateFactory(configureTestServices: services =>
        {
            services.RemoveAll<IOssService>();
            services.AddSingleton(oss.Object);
        });
        var (fileId, parseId, imageId) = await SeedParsedDocumentAsync(factory, withImage: true);
        try
        {
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/admin/document-parses/{parseId}/images/{imageId}/content");
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", CreateToken("admin"));

            var exception = await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync(request));
            Assert.Contains(CanaryMessage, UnwrapChain(exception));
        }
        finally
        {
            await DeleteSeededAsync(factory, fileId, parseId, imageId);
        }
    }

    [Fact]
    public async Task UnmarkedStreamFailure_AfterResponseStart_KeepsExistingStreamBehavior()
    {
        // An unmarked stream failing mid-body keeps today's behavior: status and content-type
        // are never rewritten into a problem, and the failure surfaces to the caller
        // unconverted. (TestServer discards already-flushed bytes when the server side
        // faults, so the sent-byte content itself is not asserted here.)
        var oss = new Mock<IOssService>();
        oss.Setup(service => service.DownloadAsync(It.IsAny<string>()))
            .ReturnsAsync(() => (Stream)new FailingAfterFirstChunkStream(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));

        using var factory = CreateFactory(configureTestServices: services =>
        {
            services.RemoveAll<IOssService>();
            services.AddSingleton(oss.Object);
        });
        var (fileId, parseId, imageId) = await SeedParsedDocumentAsync(factory, withImage: true);
        try
        {
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/admin/document-parses/{parseId}/images/{imageId}/content");
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", CreateToken("admin"));

            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);

            var received = new MemoryStream();
            var surfaced = false;
            try
            {
                await (await response.Content.ReadAsStreamAsync()).CopyToAsync(received);
            }
            catch (Exception exception)
            {
                surfaced = exception is IOException or InvalidOperationException
                    || exception.InnerException is IOException or InvalidOperationException;
            }

            Assert.True(
                surfaced, "the unmarked stream failure must surface to the caller unconverted");
            var receivedText = Encoding.UTF8.GetString(received.ToArray());
            Assert.DoesNotContain("problem+json", receivedText);
            Assert.DoesNotContain("errorCode", receivedText);
        }
        finally
        {
            await DeleteSeededAsync(factory, fileId, parseId, imageId);
        }
    }

    // ── concurrency: per-request correlation ids never cross ──

    [Fact]
    public async Task ConcurrentProblemRequests_KeepTheirOwnCorrelationIds()
    {
        using var factory = CreateFactory(configureTestServices: services =>
            UseProbeFileRepository(services, _ => throw CanaryException()));
        using var client = factory.CreateClient();

        var ids = new[] { "problem-iso-a", "problem-iso-b" };
        var responses = await Task.WhenAll(ids.Select(async id =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ListRoute);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", CreateToken("admin"));
            request.Headers.Add(CorrelationHeaderName, id);
            var response = await client.SendAsync(request);
            return (Id: id, Response: response, Body: await response.Content.ReadAsStringAsync());
        }));

        foreach (var (id, response, body) in responses)
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var root = JsonDocument.Parse(body).RootElement;
            Assert.Equal("http.internal_server_error", root.GetProperty("errorCode").GetString());
            Assert.Equal(id, root.GetProperty("correlationId").GetString());
            Assert.Equal(id, AllValues(response, CorrelationHeaderName).Single());
            // No cross-talk: the other request's id never appears in this response.
            foreach (var other in ids.Where(candidate => candidate != id))
            {
                Assert.DoesNotContain(other, body);
            }

            response.Dispose();
        }
    }

    // ── helpers ──

    private static InvalidOperationException CanaryException()
    {
        var exception = new InvalidOperationException(
            CanaryMessage, new InvalidOperationException(InnerCanary));
        exception.Data["detail"] = CanaryData;
        return exception;
    }

    /// <summary>
    /// Replaces the document-file repository with a test double whose list behavior is
    /// injected per test. The marked <c>GET /admin/document-files</c> endpoint resolves
    /// IDocumentFileService → IDocumentFileRepository with no catch on the way, so the
    /// double's failure escapes to the Problem Details boundary through the real pipeline —
    /// no production probe endpoint is needed.
    /// </summary>
    private static void UseProbeFileRepository(
        IServiceCollection services,
        Func<IHttpContextAccessor, Task<(List<DocumentFileModel> Items, int TotalCount)>> behavior)
    {
        services.AddHttpContextAccessor();
        services.RemoveAll<IDocumentFileRepository>();
        services.AddScoped<IDocumentFileRepository>(serviceProvider =>
        {
            var accessor = serviceProvider.GetRequiredService<IHttpContextAccessor>();
            return new ProbeDocumentFileRepository(() => behavior(accessor));
        });
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string url, string role)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(role));
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendPostAsync(
        HttpClient client, string url, HttpContent content, string role)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(role));
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendMultipartAsync(
        HttpClient client, string url, string role)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("probe"), "probe");
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(role));
        return await client.SendAsync(request);
    }

    /// <summary>
    /// Asserts a response is the untouched business JSON contract: the expected status,
    /// <c>application/json</c> (never <c>application/problem+json</c>), the expected body
    /// fragment, and the six-header baseline.
    /// </summary>
    private static async Task AssertBusinessJsonAsync(
        HttpResponseMessage response, HttpStatusCode expectedStatus, string bodyFragment)
    {
        using (response)
        {
            Assert.Equal(expectedStatus, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains(bodyFragment, body);
            Assert.DoesNotContain("urn:servicemantle:error", body);
            AssertBaseline(response);
        }
    }

    private static void AssertBaseline(HttpResponseMessage response)
    {
        foreach (var (name, value) in Baseline)
        {
            Assert.Equal(value, AllValues(response, name).Single());
        }
    }

    private static void AssertNoBaseline(HttpResponseMessage response)
    {
        Assert.Empty(AllValues(response, "Content-Security-Policy"));
        Assert.Empty(AllValues(response, "X-Frame-Options"));
    }

    private static IEnumerable<string> AllValues(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var headerValues))
        {
            foreach (var value in headerValues)
            {
                yield return value;
            }
        }

        if (response.Content is not null &&
            response.Content.Headers.TryGetValues(name, out var contentValues))
        {
            foreach (var value in contentValues)
            {
                yield return value;
            }
        }
    }

    private static string UnwrapChain(Exception exception)
    {
        var builder = new StringBuilder();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            builder.Append(current.GetType().Name).Append(':').Append(current.Message).Append('|');
        }

        return builder.ToString();
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

    private static async Task<(Guid FileId, Guid ParseId, Guid? ImageId)> SeedParsedDocumentAsync(
        WebApplicationFactory<Program> factory, bool withImage)
    {
        var fileId = Guid.NewGuid();
        var parseId = Guid.NewGuid();
        Guid? imageId = withImage ? Guid.NewGuid() : null;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        db.DocumentFiles.Add(new DocumentFileEntity
        {
            Id = fileId,
            FileName = "problem-details-probe.md",
            ContentType = "text/markdown"
        });
        db.DocumentParses.Add(new DocumentParseEntity
        {
            Id = parseId,
            DocumentFileId = fileId,
            ModelVersion = "vlm",
            Status = DocumentParseStatus.Parsed,
            MarkdownContent = "# problem-details-probe"
        });
        if (imageId is Guid id)
        {
            db.DocumentParseImages.Add(new DocumentParseImageEntity
            {
                Id = id,
                ParseId = parseId,
                ImageName = "probe.png",
                ImagePath = "legacy/probe.png",
                ContentType = "image/png"
            });
        }

        await db.SaveChangesAsync();
        return (fileId, parseId, imageId);
    }

    private static async Task<(Guid FileId, Guid ParseId)> SeedFileWithParsingParseAsync(
        WebApplicationFactory<Program> factory)
    {
        var fileId = Guid.NewGuid();
        var parseId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        db.DocumentFiles.Add(new DocumentFileEntity
        {
            Id = fileId,
            FileName = "problem-in-progress-probe.md",
            ContentType = "text/markdown"
        });
        db.DocumentParses.Add(new DocumentParseEntity
        {
            Id = parseId,
            DocumentFileId = fileId,
            ModelVersion = "vlm",
            Status = DocumentParseStatus.Parsing
        });
        await db.SaveChangesAsync();
        return (fileId, parseId);
    }

    private static async Task DeleteSeededAsync(
        WebApplicationFactory<Program> factory, Guid fileId, Guid parseId, Guid? imageId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        if (imageId is Guid id)
        {
            db.DocumentParseImages.RemoveRange(
                db.DocumentParseImages.Where(image => image.Id == id));
        }

        db.DocumentParses.RemoveRange(db.DocumentParses.Where(parse => parse.Id == parseId));
        db.DocumentFiles.RemoveRange(db.DocumentFiles.Where(file => file.Id == fileId));
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Repository double whose <c>GetListAsync</c> runs the injected behavior (throw, cancel,
    /// or write a partial response); every other member is inert.
    /// </summary>
    private sealed class ProbeDocumentFileRepository(
        Func<Task<(List<DocumentFileModel> Items, int TotalCount)>> getListBehavior)
        : IDocumentFileRepository
    {
        public Task AddAsync(DocumentFileModel model) => Task.CompletedTask;

        public Task<DocumentFileModel?> GetByIdAsync(Guid id) =>
            Task.FromResult<DocumentFileModel?>(null);

        public Task<(List<DocumentFileModel> Items, int TotalCount)> GetListAsync(
            int page, int size, string? fileName = null) => getListBehavior();

        public Task<bool> UpdateAsync(DocumentFileModel model) => Task.FromResult(false);

        public Task<bool> UpdateMetadataAsync(Guid id, string? subject, string? grade, string? year) =>
            Task.FromResult(false);

        public Task<bool> AttachStructaDocDocumentAsync(Guid id, Guid structaDocDocumentId) =>
            Task.FromResult(false);

        public Task<bool> DeleteAsync(Guid id) => Task.FromResult(false);
    }

    /// <summary>
    /// Returns one chunk of bytes, then fails: the response has started (headers plus bytes
    /// were flushed) before the failure surfaces, reproducing the unmarked mid-stream case.
    /// </summary>
    private sealed class FailingAfterFirstChunkStream(byte[] firstChunk) : Stream
    {
        private int _reads;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Interlocked.Increment(ref _reads) > 1)
            {
                throw new IOException(StreamCanary);
            }

            var length = Math.Min(count, firstChunk.Length);
            Array.Copy(firstChunk, 0, buffer, offset, length);
            return length;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) > 1)
            {
                throw new IOException(StreamCanary);
            }

            var length = Math.Min(buffer.Length, firstChunk.Length);
            firstChunk.AsSpan(0, length).CopyTo(buffer.Span);
            return ValueTask.FromResult(length);
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
