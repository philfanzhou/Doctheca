using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;
using Doctheca.Service.Parsing;
using Doctheca.Service.StructaDoc;
using Xunit;

namespace Doctheca.Tests.Parsing;

public class StructaDocParseResultSyncTests
{
    private static readonly Guid ParseId = Guid.NewGuid();
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid AssetId = Guid.NewGuid();

    [Fact]
    public async Task SyncAsync_CreatesImageRecordsFromAssets()
    {
        var parseService = new Mock<IDocumentParseService>();
        var (sync, _) = CreateSync(parseService, assets:
        [
            new StructaDocAssetResponse { Id = AssetId, Name = "img-1.jpg", MediaType = "image/png", SizeBytes = 10 },
            new StructaDocAssetResponse { Id = Guid.NewGuid(), Name = "img-2.bin", MediaType = "", SizeBytes = 5 },
        ]);

        await sync.SyncAsync(CreateParse(), RunId, CancellationToken.None);

        parseService.Verify(x => x.AddImageAsync(It.IsAny<DocumentParseImageModel>()), Times.Exactly(2));
        parseService.Verify(x => x.AddImageAsync(It.Is<DocumentParseImageModel>(img =>
            img.ParseId == ParseId
            && img.ImageName == "img-1.jpg"
            && img.ImagePath == AssetId.ToString("D")
            && img.ContentType == "image/png")), Times.Once);
        // Empty media type falls back to the column default.
        parseService.Verify(x => x.AddImageAsync(It.Is<DocumentParseImageModel>(img =>
            img.ContentType == "image/jpeg")), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_ReplacesPreviousImagesBeforeInsert()
    {
        var imageRepository = new Mock<IDocumentParseImageRepository>();
        var (sync, _) = CreateSync(imageRepository: imageRepository);

        await sync.SyncAsync(CreateParse(), RunId, CancellationToken.None);

        imageRepository.Verify(x => x.DeleteByParseIdAsync(ParseId), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_MapsBlocksToLocalConventions()
    {
        List<DocumentParseBlockModel>? inserted = null;
        var blockRepository = new Mock<IDocumentParseBlockRepository>();
        blockRepository.Setup(x => x.AddBlocksAsync(ParseId, It.IsAny<IEnumerable<DocumentParseBlockModel>>()))
            .Callback<Guid, IEnumerable<DocumentParseBlockModel>>((_, blocks) => inserted = blocks.ToList())
            .Returns(Task.CompletedTask);

        var blocks = new List<StructaDocBlockResponse>
        {
            new()
            {
                Sequence = 1,
                PageNumber = 3,
                Type = "title",
                Subtype = "heading-2",
                Content = "Chapter",
                ContentFormat = "plain",
                BoundingBox = new StructaDocBoundingBox { X0 = 0.1, Y0 = 0.2, X1 = 0.5, Y1 = 0.25 },
                Confidence = 0.87,
            },
            new()
            {
                Sequence = 0,
                PageNumber = 3,
                Type = "text",
                Content = "Body text",
            },
            new()
            {
                Sequence = 2,
                PageNumber = null,
                Type = "image",
                AssetId = AssetId,
            },
        };

        var (sync, _) = CreateSync(
            blockRepository: blockRepository,
            blocks: blocks,
            assets: [new StructaDocAssetResponse { Id = AssetId, Name = "img.jpg", MediaType = "image/jpeg" }]);

        await sync.SyncAsync(CreateParse(), RunId, CancellationToken.None);

        inserted.Should().NotBeNull();
        inserted.Should().HaveCount(3);

        // Ordered by sequence; sort index is per page.
        var first = inserted![0];
        first.TextContent.Should().Be("Body text");
        first.PageId.Should().Be(2); // 1-based page 3 → 0-based legacy convention
        first.SortIndex.Should().Be(0);
        first.TextLevel.Should().Be(-1);

        var second = inserted[1];
        second.BlockType.Should().Be("title");
        second.SubType.Should().Be("heading-2");
        second.TextLevel.Should().Be(2);
        second.TextFormat.Should().Be("plain");
        second.SortIndex.Should().Be(1);
        second.MineruScore.Should().Be(0.87);
        // 0-1 normalized box scaled to the local 0-1000 convention.
        second.BboxX0.Should().BeApproximately(100f, 0.01f);
        second.BboxY1.Should().BeApproximately(250f, 0.01f);
        second.BlockData.Should().Contain("\"sequence\":1");

        var third = inserted[2];
        third.PageId.Should().Be(0); // null page → single virtual page 0
        third.ImageId.Should().NotBeNull();
        third.BlockType.Should().Be("image");
    }

    [Fact]
    public async Task SyncAsync_MarksParseParsedWithMarkdownAndRunId()
    {
        var parseService = new Mock<IDocumentParseService>();
        var (sync, _) = CreateSync(parseService, markdown: "# Result");

        var result = await sync.SyncAsync(CreateParse(), RunId, CancellationToken.None);

        result.Should().Be("# Result");
        parseService.Verify(x => x.UpdateStatusAsync(
            ParseId,
            DocumentParseStatus.Parsed,
            null,
            "# Result",
            null, null, null, null, null, null,
            RunId), Times.Once);
    }

    // ── helpers ──

    private static DocumentParseModel CreateParse() => new()
    {
        Id = ParseId,
        DocumentFileId = Guid.NewGuid(),
        Status = DocumentParseStatus.Parsing,
        StructaDocParseRunId = RunId,
    };

    private static (StructaDocParseResultSync Sync, Mock<IStructaDocClient> Client) CreateSync(
        Mock<IDocumentParseService>? parseService = null,
        Mock<IDocumentParseImageRepository>? imageRepository = null,
        Mock<IDocumentParseBlockRepository>? blockRepository = null,
        List<StructaDocAssetResponse>? assets = null,
        List<StructaDocBlockResponse>? blocks = null,
        string markdown = "# markdown")
    {
        var client = new Mock<IStructaDocClient>();
        client.Setup(x => x.GetAssetsAsync(RunId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(assets ?? []);
        client.Setup(x => x.GetAllBlocksAsync(RunId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blocks ?? []);
        client.Setup(x => x.GetMarkdownAsync(RunId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(markdown);

        parseService ??= new Mock<IDocumentParseService>();
        imageRepository ??= new Mock<IDocumentParseImageRepository>();
        blockRepository ??= new Mock<IDocumentParseBlockRepository>();

        var sync = new StructaDocParseResultSync(
            client.Object,
            parseService.Object,
            blockRepository.Object,
            imageRepository.Object,
            NullLogger<StructaDocParseResultSync>.Instance);

        return (sync, client);
    }
}
