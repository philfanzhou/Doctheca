using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Doctheca.Common.ObjectStorage;
using Doctheca.Domain.Models;
using Doctheca.Service.Parsing;
using Doctheca.Service.StructaDoc;
using Xunit;

namespace Doctheca.Tests.Parsing;

public class ImageContentSourceTests
{
    [Fact]
    public async Task LegacyParse_ReadsFromOss()
    {
        var objectStore = new Mock<IObjectStore>();
        var expected = new MemoryStream([9]);
        objectStore.Setup(x => x.DownloadAsync("documents/mineru/task/img.jpg"))
            .ReturnsAsync(expected);
        var client = new Mock<IStructaDocClient>();
        var source = CreateSource(client, objectStore);

        var parse = new DocumentParse { StructaDocParseRunId = null };
        var image = new DocumentParseImage { ImagePath = "documents/mineru/task/img.jpg" };

        var stream = await source.OpenAsync(parse, image);

        stream.Should().BeSameAs(expected);
        client.Verify(
            x => x.GetAssetContentAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StructaDocParse_StreamsAssetContent()
    {
        var runId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var expected = new MemoryStream([7]);
        var client = new Mock<IStructaDocClient>();
        client.Setup(x => x.GetAssetContentAsync(runId, assetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var source = CreateSource(client, new Mock<IObjectStore>());

        var parse = new DocumentParse { StructaDocParseRunId = runId };
        var image = new DocumentParseImage { ImagePath = assetId.ToString("D") };

        var stream = await source.OpenAsync(parse, image);

        stream.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task StructaDocParse_InvalidAssetReference_ReturnsNull()
    {
        var source = CreateSource(new Mock<IStructaDocClient>(), new Mock<IObjectStore>());

        var parse = new DocumentParse { StructaDocParseRunId = Guid.NewGuid() };
        var image = new DocumentParseImage { ImagePath = "not-a-guid" };

        var stream = await source.OpenAsync(parse, image);

        stream.Should().BeNull();
    }

    [Fact]
    public async Task LegacyPostAwaitCancellation_DisposesBufferedDownload()
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new MemoryStream([1]);
        var oss = new Mock<IObjectStore>();
        oss.Setup(service => service.DownloadAsync(It.IsAny<string>())).Returns(() =>
        {
            cancellation.Cancel();
            return Task.FromResult<Stream>(stream);
        });
        var source = CreateSource(new Mock<IStructaDocClient>(), oss);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.OpenAsync(new(), new() { ImagePath = "synthetic" }, cancellation.Token));
        Assert.False(stream.CanRead);
    }

    [Fact]
    public async Task PreCancelledRead_DoesNotOpenEitherSource()
    {
        var oss = new Mock<IObjectStore>(MockBehavior.Strict);
        var remote = new Mock<IStructaDocClient>(MockBehavior.Strict);
        var source = CreateSource(remote, oss);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        foreach (var parse in new[] { new DocumentParse(), new DocumentParse { StructaDocParseRunId = Guid.NewGuid() } })
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.OpenAsync(parse, new() { ImagePath = "synthetic" }, cancellation.Token));
        oss.VerifyNoOtherCalls(); remote.VerifyNoOtherCalls();
    }

    private static ImageContentSource CreateSource(
        Mock<IStructaDocClient> client, Mock<IObjectStore> objectStore) =>
        new(client.Object, objectStore.Object, NullLogger<ImageContentSource>.Instance);
}
