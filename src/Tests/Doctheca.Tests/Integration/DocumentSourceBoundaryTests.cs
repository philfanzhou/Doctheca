using System.Net;
using System.Net.Http.Json;
using Doctheca.Database;
using Doctheca.Database.Repositories;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services.Structure;
using Doctheca.Service.StructaDoc;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class DocumentStructureEndpointTests
{
    [Theory]
    [InlineData("failed", true)] [InlineData("uncertain-reference", false)] [InlineData("committed", false)] [InlineData("other-reference", false)]
    public async Task AttachmentFailure_OnlyDeletesAProvenUnreferencedNewSource(string outcome, bool delete)
    {
        using var original = AuthoringFactory(true); using var originalClient = Admin(original);
        var id = (await ReadData(await originalClient.PostAsJsonAsync("/admin/document-files", new { fileName = "Retained book" })))!["id"]!.GetValue<string>();
        var source = Guid.NewGuid(); var repository = new Mock<IDocumentAuthoringRepository>();
        if (outcome == "other-reference") { using var scope = original.Services.CreateScope(); var otherDb = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>(); otherDb.DocumentFiles.Add(new() { FileName = "Other source owner", StructaDocDocumentId = source, ContentType = "application/pdf" }); await otherDb.SaveChangesAsync(); }
        _authoringUpstream.Setup(u => u.UploadDocumentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(new StructaDocDocumentResponse { Id = source });
        _authoringUpstream.Setup(u => u.DeleteDocumentAsync(source, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        repository.Setup(r => r.AttachSourceAsync(Guid.Parse(id), source, "application/pdf", It.IsAny<CancellationToken>())).Returns(async () =>
        {
            if (outcome == "committed") { using var scope = original.Services.CreateScope(); await new DocumentAuthoringRepository(scope.ServiceProvider.GetRequiredService<DocthecaDbContext>()).AttachSourceAsync(Guid.Parse(id), source, "application/pdf", default); }
            throw new InvalidOperationException("Synthetic local outcome");
        });
        repository.Setup(r => r.IsSourceReferencedAsync(Guid.Parse(id), source, It.IsAny<CancellationToken>())).Returns(async () =>
        {
            if (outcome == "uncertain-reference") throw new InvalidOperationException("Synthetic unavailable reference read");
            using var scope = original.Services.CreateScope(); return await new DocumentAuthoringRepository(scope.ServiceProvider.GetRequiredService<DocthecaDbContext>()).IsSourceReferencedAsync(Guid.Parse(id), source, default);
        });
        using var failed = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => { services.RemoveAll<IDocumentAuthoringRepository>(); services.AddSingleton(repository.Object); }));
        using var client = Admin(failed);
        var response = await client.PostAsync($"/admin/document-files/{id}/source", Pdf());
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        _authoringUpstream.Verify(u => u.DeleteDocumentAsync(source, It.IsAny<CancellationToken>()), delete ? Times.Once() : Times.Never());
        using var finalScope = original.Services.CreateScope(); var db = finalScope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var book = await db.DocumentFiles.AsNoTracking().SingleAsync(f => f.Id == Guid.Parse(id)); book.FileName.Should().Be("Retained book");
        book.StructaDocDocumentId.Should().Be(outcome == "committed" ? source : null);
    }

    [Theory]
    [InlineData(413, 400)] [InlineData(415, 400)] [InlineData(401, 502)] [InlineData(500, 502)]
    public async Task UpstreamRejection_IsRedactedAndLeavesBookUntouched(int upstreamStatus, int expected)
    {
        using var factory = AuthoringFactory(true); using var client = Admin(factory);
        var id = (await ReadData(await client.PostAsJsonAsync("/admin/document-files", new { fileName = "Unchanged" })))!["id"]!.GetValue<string>();
        _authoringUpstream.Setup(u => u.UploadDocumentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StructaDocException("SYNTHETIC_PROVIDER_DIAGNOSTIC_MUST_NOT_ESCAPE", upstreamStatus, false));
        var response = await client.PostAsync($"/admin/document-files/{id}/source", Pdf());
        ((int)response.StatusCode).Should().Be(expected); (await response.Content.ReadAsStringAsync()).Should().NotContain("SYNTHETIC_PROVIDER");
        var detail = (await ReadData(await client.GetAsync($"/admin/document-files/{id}")))!; detail["hasSource"]!.GetValue<bool>().Should().BeFalse(); detail["parses"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task BookDeletedWhileUploading_DoesNotRecreateIt_AndCleansOnlyTheNewRemote()
    {
        using var factory = AuthoringFactory(true); using var client = Admin(factory);
        var id = (await ReadData(await client.PostAsJsonAsync("/admin/document-files", new { fileName = "Delete race" })))!["id"]!.GetValue<string>();
        var source = Guid.NewGuid(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _authoringUpstream.Setup(u => u.UploadDocumentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { entered.TrySetResult(); await resume.Task; return new() { Id = source }; });
        _authoringUpstream.Setup(u => u.DeleteDocumentAsync(source, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var attaching = client.PostAsync($"/admin/document-files/{id}/source", Pdf()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { (await client.DeleteAsync($"/admin/document-files/{id}")).StatusCode.Should().Be(HttpStatusCode.OK); }
        finally { resume.TrySetResult(); }
        (await attaching).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _authoringUpstream.Verify(u => u.DeleteDocumentAsync(source, It.IsAny<CancellationToken>()), Times.Once);
        (await client.GetAsync($"/admin/document-files/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CancelledUpload_DisposesStreamAndDoesNotAttachOrEnqueue()
    {
        using var factory = AuthoringFactory(true); using var client = Admin(factory);
        var id = (await ReadData(await client.PostAsJsonAsync("/admin/document-files", new { fileName = "Cancelled" })))!["id"]!.GetValue<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Stream? input = null;
        _authoringUpstream.Setup(u => u.UploadDocumentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, Stream stream, CancellationToken ct) => { input = stream; entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, ct); } finally { finished.TrySetResult(); } return new(); });
        using var cancel = new CancellationTokenSource(); var uploading = client.PostAsync($"/admin/document-files/{id}/source", Pdf(), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uploading); await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Wait for request disposal to finish, without assuming that CanRead reflects ReferenceReadStream.Dispose.
        for (var attempt = 0; attempt < 100; attempt++) { try { input!.ReadByte(); await Task.Yield(); } catch (ObjectDisposedException) { break; } }
        Assert.Throws<ObjectDisposedException>(() => input!.ReadByte());
        (await ReadData(await client.GetAsync($"/admin/document-files/{id}")))!["hasSource"]!.GetValue<bool>().Should().BeFalse();
        _authoringUpstream.Verify(u => u.DeleteDocumentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task OversizedMultipart_IsRejectedBeforeRemoteUpload()
    {
        using var factory = AuthoringFactory(true); using var client = Admin(factory);
        var id = (await ReadData(await client.PostAsJsonAsync("/admin/document-files", new { fileName = "Size limit" })))!["id"]!.GetValue<string>();
        using var form = new MultipartFormDataContent(); using var bytes = new StreamContent(new RepeatedBytes(200L * 1024 * 1024 + 1));
        bytes.Headers.ContentType = new("application/pdf"); form.Add(bytes, "file", "large.pdf");
        (await client.PostAsync($"/admin/document-files/{id}/source", form)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _authoringUpstream.Invocations.Should().BeEmpty();
    }
    private sealed class RepeatedBytes(long remaining) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var take = (int)Math.Min(remaining, count); Array.Clear(buffer, offset, take); remaining -= take; return take; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); var take = (int)Math.Min(remaining, buffer.Length); buffer.Span[..take].Clear(); remaining -= take; return ValueTask.FromResult(take); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long length) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

}
