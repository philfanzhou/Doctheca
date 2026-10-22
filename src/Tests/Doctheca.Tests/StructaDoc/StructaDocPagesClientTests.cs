using System.Net;
using System.Text;
using Doctheca.Service.StructaDoc;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Doctheca.Tests.StructaDoc;

public sealed class StructaDocPagesClientTests
{
    [Fact]
    public async Task BareArray_UsesVersionedAuthenticatedEndpointAndPreservesLabels()
    {
        var run = Guid.NewGuid();
        var client = Client((request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.AbsolutePath.Should().Be($"/api/v1/parse-runs/{run:D}/pages");
            request.Headers.Authorization!.Scheme.Should().Be("ApiKey");
            request.Headers.Authorization.Parameter.Should().Be("synthetic-pages-credential");
            return Task.FromResult(Json("""
                [{"number":1,"label":"iv","labelSource":"pdf-page-labels","width":600},
                 {"number":2,"label":"iv","labelSource":"pdf-page-labels"},
                 {"number":3,"label":"封面\n一","labelSource":"page-number-blocks"},
                 {"number":4}, {"number":5,"label":"","labelSource":""}]
                """));
        });
        var pages = await client.GetPagesAsync(run);
        pages.Select(page => page.Number).Should().Equal(1, 2, 3, 4, 5);
        pages.Select(page => page.Label).Should().Equal("iv", "iv", "封面\n一", null, "");
        pages[3].LabelSource.Should().BeNull();
        pages[4].LabelSource.Should().BeEmpty();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"items\":[]}")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"number\":0}]")]
    [InlineData("[{\"number\":-1}]")]
    [InlineData("[{\"number\":2147483648}]")]
    [InlineData("[{\"number\":1.5}]")]
    [InlineData("[{\"number\":\"1\"}]")]
    [InlineData("[{\"number\":1},{\"number\":1}]")]
    [InlineData("[{\"number\":1,\"label\":{\"content\":\"synthetic-document-canary\"}}]")]
    public async Task MalformedSet_FailsAsAWholeWithSafeDiagnostics(string payload)
    {
        var client = Client((_, _) => Task.FromResult(Json(payload)));
        var error = await Assert.ThrowsAsync<StructaDocException>(() => client.GetPagesAsync(Guid.NewGuid()));
        error.Message.Should().NotContain("synthetic-document-canary");
        error.InnerException.Should().BeNull();
    }

    [Fact]
    public async Task EmptyArray_IsAValidEmptyResult() =>
        (await Client((_, _) => Task.FromResult(Json("[]"))).GetPagesAsync(Guid.NewGuid())).Should().BeEmpty();

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task HttpFailure_DoesNotReadUpstreamDiagnosticBody(HttpStatusCode status)
    {
        var content = new UnreadableContent();
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = content }));
        var error = await Assert.ThrowsAsync<StructaDocException>(() => client.GetPagesAsync(Guid.NewGuid()));
        error.StatusCode.Should().Be((int)status);
        error.Message.Should().NotContain("synthetic-upstream-canary");
        content.Reads.Should().Be(0);
        content.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task CallerCancellation_ReachesHttpAndPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        var client = Client(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return Json("[]");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetPagesAsync(Guid.NewGuid(), cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyRead_RespectsTimeoutOrCallerCancellationAndReleasesResponse(bool callerCancellation)
    {
        using var cancellation = new CancellationTokenSource();
        var content = new DelayedContent();
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }),
            callerCancellation ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(500));
        var request = client.GetPagesAsync(Guid.NewGuid(), cancellation.Token);
        await content.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (callerCancellation)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }
        else
        {
            var error = await Assert.ThrowsAsync<StructaDocException>(() => request);
            error.IsTransient.Should().BeTrue();
            error.Message.Should().Be("StructaDoc pages request timed out.");
        }
        content.Disposed.Should().BeTrue();
    }

    private static StructaDocClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler, TimeSpan? timeout = null) =>
        new(new HttpClient(new Handler(handler)) { BaseAddress = new Uri("http://pages.test/"), Timeout = timeout ?? TimeSpan.FromSeconds(100) },
            Options.Create(new StructaDocOptions { ApiKey = "synthetic-pages-credential" }), NullLogger<StructaDocClient>.Instance);

    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
    { Content = new StringContent(payload, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class UnreadableContent : HttpContent
    {
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            Reads++;
            throw new InvalidOperationException("synthetic-upstream-canary");
        }
        protected override bool TryComputeLength(out long length) { length = 1; return true; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class DelayedContent : HttpContent
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            throw new InvalidOperationException("A cancellable body read is required.");
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
        { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
        protected override bool TryComputeLength(out long length) { length = 1; return true; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
