using System.Net;
using Doctheca.Service.StructaDoc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Doctheca.Tests.StructaDoc;

public sealed class StructaDocImageStreamTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturnedStream_OwnsHttpResponseForBothDisposalPaths(bool asynchronous)
    {
        var body = new Content(new MemoryStream([1, 2, 3]));
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = body };
        var client = Client(response);
        var stream = await client.GetAssetContentAsync(Guid.NewGuid(), Guid.NewGuid());
        Assert.False(body.Disposed);
        Assert.Equal(1, stream.ReadByte());
        if (asynchronous) await stream.DisposeAsync(); else stream.Dispose();
        Assert.True(body.Disposed);
        stream.Dispose();
        Assert.Equal(1, body.DisposeCalls);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(503)]
    [InlineData(401)]
    public async Task ErrorResponses_AreDisposedWithoutReadingProviderBody(int status)
    {
        var body = new Content(new MemoryStream()) { FailOpen = true };
        var client = Client(new HttpResponseMessage((HttpStatusCode)status) { Content = body });
        var error = await Assert.ThrowsAsync<StructaDocException>(() => client.GetAssetContentAsync(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal("StructaDoc image content request failed.", error.Message);
        Assert.True(body.Disposed);
        Assert.False(body.Opened);
    }

    [Fact]
    public async Task FailureOpeningStream_DisposesResponse()
    {
        var body = new Content(new MemoryStream()) { FailOpen = true };
        var client = Client(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        await Assert.ThrowsAsync<IOException>(() => client.GetAssetContentAsync(Guid.NewGuid(), Guid.NewGuid()));
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task CancellationAfterStreamOpen_DisposesResponseAndDoesNotReturnSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var body = new Content(new MemoryStream()) { OnOpen = cancellation.Cancel };
        var client = Client(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAssetContentAsync(Guid.NewGuid(), Guid.NewGuid(), cancellation.Token));
        Assert.True(body.Disposed);
    }

    private static StructaDocClient Client(HttpResponseMessage response) => new(new HttpClient(new Handler(response)) { BaseAddress = new Uri("http://upstream.invalid/") }, Options.Create(new StructaDocOptions()), NullLogger<StructaDocClient>.Instance);
    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }
    private sealed class Content(Stream stream) : HttpContent
    {
        public bool Disposed;
        public int DisposeCalls;
        public bool FailOpen;
        public bool Opened;
        public Action? OnOpen;
        protected override Task<Stream> CreateContentReadStreamAsync() => Open();
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Open();
        private Task<Stream> Open()
        {
            Opened = true;
            OnOpen?.Invoke();
            return FailOpen ? Task.FromException<Stream>(new IOException("private-canary")) : Task.FromResult(stream);
        }
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => throw new InvalidOperationException("Provider error body must not be consumed.");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed = true; DisposeCalls++; stream.Dispose(); base.Dispose(disposing); }
    }
}
