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

        var completion = parseService.Invocations.Single(call => call.Method.Name == nameof(IDocumentParseService.CompleteAsync));
        var images = (IReadOnlyList<DocumentParseImageModel>)completion.Arguments[4]!;
        images.Should().HaveCount(2);
        images[0].ParseId.Should().Be(ParseId);
        images[0].ImageName.Should().Be("img-1.jpg");
        images[0].ImagePath.Should().Be(AssetId.ToString("D"));
        images[0].ContentType.Should().Be("image/png");
        images[1].ContentType.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task SyncAsync_EmptyResultsAreStillOneAtomicReplacement()
    {
        var service = new Mock<IDocumentParseService>();
        var (sync, _) = CreateSync(service);
        await sync.SyncAsync(CreateParse(), RunId, CancellationToken.None);
        var call = service.Invocations.Single(item => item.Method.Name == nameof(IDocumentParseService.CompleteAsync));
        ((IReadOnlyCollection<DocumentParsePageModel>)call.Arguments[3]!).Should().BeEmpty();
        ((IReadOnlyList<DocumentParseImageModel>)call.Arguments[4]!).Should().BeEmpty();
        ((IReadOnlyList<DocumentParseBlockModel>)call.Arguments[5]!).Should().BeEmpty();
    }

    [Fact]
    public async Task SyncAsync_MapsBlocksToLocalConventions()
    {
        List<DocumentParseBlockModel>? inserted = null;
        var parseService = CompletionCapture(rows => inserted = rows);

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
            parseService: parseService,
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
        // The global StructaDoc order is stored as-is (issue #89); sort_index stays per page.
        first.Sequence.Should().Be(0);
        first.TextLevel.Should().Be(-1);

        var second = inserted[1];
        second.BlockType.Should().Be("title");
        second.SubType.Should().Be("heading-2");
        second.TextLevel.Should().Be(2);
        second.TextFormat.Should().Be("plain");
        second.SortIndex.Should().Be(1);
        second.Sequence.Should().Be(1);
        second.MineruScore.Should().Be(0.87);
        // 0-1 normalized box scaled to the local 0-1000 convention.
        second.BboxX0.Should().BeApproximately(100f, 0.01f);
        second.BboxY1.Should().BeApproximately(250f, 0.01f);
        second.BlockData.Should().Contain("\"sequence\":1");

        var third = inserted[2];
        third.PageId.Should().Be(0); // null page → single virtual page 0
        third.Sequence.Should().Be(2);
        third.SortIndex.Should().Be(0);
        third.ImageId.Should().NotBeNull();
        third.BlockType.Should().Be("image");
    }

    [Fact]
    public async Task SyncAsync_ClassifiesUsingFullTypeAndSubtypeBeforeStorageTruncation()
    {
        var longSubtype = "heading-" + new string('0', 60) + "2";
        List<DocumentParseBlockModel>? inserted = null;
        var service = CompletionCapture(rows => inserted = rows);
        var (sync, _) = CreateSync(parseService: service, blocks:
        [
            new() { Sequence = 2, PageNumber = 2, Type = "text", Subtype = "heading-+3", Content = "compatible" },
            new() { Sequence = 0, PageNumber = 2, Type = "title", Content = "level-less" },
            new() { Sequence = 1, PageNumber = 2, Type = "title", Subtype = longSubtype, Content = "numbered" },
            new() { Sequence = 3, Type = "TITLE", Subtype = "malformed", Content = "legacy type" }
        ]);
        await sync.SyncAsync(CreateParse(), RunId, default);
        Assert.NotNull(inserted);
        inserted.Select(row => row.TextLevel).Should().Equal(-2, 2, 3, -1);
        inserted.Take(3).Select(row => row.PageId).Should().Equal(1, 1, 1);
        inserted.Take(3).Select(row => row.SortIndex).Should().Equal(0, 1, 2);
        inserted.Select(row => row.Sequence).Should().Equal(0, 1, 2, 3);
        inserted[1].SubType.Should().Be(longSubtype[..50]);
        using var original = System.Text.Json.JsonDocument.Parse(inserted[1].BlockData);
        original.RootElement.GetProperty("subtype").GetString().Should().Be(longSubtype);
    }

    [Fact]
    public async Task SyncAsync_MarksParseParsedWithMarkdownAndRunId()
    {
        var parseService = new Mock<IDocumentParseService>();
        var (sync, _) = CreateSync(parseService, markdown: "# Result");

        var result = await sync.SyncAsync(CreateParse(), RunId, CancellationToken.None);

        result.Should().Be("# Result");
        var call = parseService.Invocations.Single(item => item.Method.Name == nameof(IDocumentParseService.CompleteAsync));
        call.Arguments[0].Should().Be(ParseId);
        call.Arguments[1].Should().Be(RunId);
        call.Arguments[2].Should().Be("# Result");
    }

    // ── helpers ──

    private static Mock<IDocumentParseService> CompletionCapture(Action<List<DocumentParseBlockModel>> capture)
    {
        var service = new Mock<IDocumentParseService>();
        service.Setup(value => value.CompleteAsync(ParseId, RunId, It.IsAny<string>(),
                It.IsAny<IReadOnlyCollection<DocumentParsePageModel>>(), It.IsAny<IReadOnlyList<DocumentParseImageModel>>(),
                It.IsAny<IReadOnlyList<DocumentParseBlockModel>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, string, IReadOnlyCollection<DocumentParsePageModel>, IReadOnlyList<DocumentParseImageModel>, IReadOnlyList<DocumentParseBlockModel>, CancellationToken>(
                (_, _, _, _, _, rows, _) => capture(rows.ToList()))
            .ReturnsAsync("# markdown");
        return service;
    }

    private static DocumentParseModel CreateParse() => new()
    {
        Id = ParseId,
        DocumentFileId = Guid.NewGuid(),
        Status = DocumentParseStatus.Parsing,
        StructaDocParseRunId = RunId,
    };

    private static (StructaDocParseResultSync Sync, Mock<IStructaDocClient> Client) CreateSync(
        Mock<IDocumentParseService>? parseService = null,
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
        parseService.SetReturnsDefault(Task.FromResult(markdown));

        var sync = new StructaDocParseResultSync(
            client.Object,
            parseService.Object,
            NullLogger<StructaDocParseResultSync>.Instance);

        return (sync, client);
    }
}
