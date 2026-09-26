using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Doctheca.Common.Oss;
using Doctheca.Domain.Models;
using Doctheca.Service.Parsing;
using Doctheca.Service.StructaDoc;
using Xunit;

namespace Doctheca.Tests.Parsing;

public class ParseImageContentSourceTests
{
    [Fact]
    public async Task LegacyParse_ReadsFromOss()
    {
        var ossService = new Mock<IOssService>();
        var expected = new MemoryStream([9]);
        ossService.Setup(x => x.DownloadAsync("documents/mineru/task/img.jpg"))
            .ReturnsAsync(expected);
        var client = new Mock<IStructaDocClient>();
        var source = CreateSource(client, ossService);

        var parse = new DocumentParseModel { StructaDocParseRunId = null };
        var image = new DocumentParseImageModel { ImagePath = "documents/mineru/task/img.jpg" };

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
        var source = CreateSource(client, new Mock<IOssService>());

        var parse = new DocumentParseModel { StructaDocParseRunId = runId };
        var image = new DocumentParseImageModel { ImagePath = assetId.ToString("D") };

        var stream = await source.OpenAsync(parse, image);

        stream.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task StructaDocParse_InvalidAssetReference_ReturnsNull()
    {
        var source = CreateSource(new Mock<IStructaDocClient>(), new Mock<IOssService>());

        var parse = new DocumentParseModel { StructaDocParseRunId = Guid.NewGuid() };
        var image = new DocumentParseImageModel { ImagePath = "not-a-guid" };

        var stream = await source.OpenAsync(parse, image);

        stream.Should().BeNull();
    }

    private static ParseImageContentSource CreateSource(
        Mock<IStructaDocClient> client, Mock<IOssService> ossService) =>
        new(client.Object, ossService.Object, NullLogger<ParseImageContentSource>.Instance);
}
