using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Database;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Service;
using Ruoyu.Study.DocLibrary.Tests.TestHelpers;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

public class QuestionBankImportServiceTests
{
    private readonly Mock<IOssService> _ossMock = new();
    private readonly Mock<ILogger<QuestionBankImportService>> _loggerMock = new();

    private QuestionBankImportService CreateService(DocLibraryDbContext context) =>
        new(context, _ossMock.Object, _loggerMock.Object);

    [Fact]
    public async Task GetImportableListAsync_OnlyReturnsParsedDocuments()
    {
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        QuestionBankTestData.SeedParsedParse(context, "parsed.pdf");
        var (pending, _) = QuestionBankTestData.SeedParsedParse(context, "pending.pdf");
        pending.Status = DocumentParseStatus.Pending;
        context.SaveChanges();

        var (items, total) = await CreateService(context).GetImportableListAsync(1, 20);

        total.Should().Be(1);
        items.Should().ContainSingle().Which.FileName.Should().Be("parsed.pdf");
    }

    [Fact]
    public async Task GetImportableListAsync_SearchMatchesFileName()
    {
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        QuestionBankTestData.SeedParsedParse(context, "english-final.pdf");
        QuestionBankTestData.SeedParsedParse(context, "math-monthly.pdf");

        var (items, total) = await CreateService(context)
            .GetImportableListAsync(1, 20, "final");

        total.Should().Be(1);
        items.Should().ContainSingle().Which.FileName.Should().Be("english-final.pdf");
    }

    [Fact]
    public void ImportableParseItem_DoesNotExposeImportTrackingFields()
    {
        typeof(ImportableParseItem).GetProperty("ImportStatus").Should().BeNull();
        typeof(ImportableParseItem).GetProperty("ImportedAt").Should().BeNull();
    }

    [Fact]
    public async Task GetImportableListAsync_OrdersByParsedAtDescending()
    {
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        QuestionBankTestData.SeedParsedParse(
            context,
            "old.pdf",
            parsedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        QuestionBankTestData.SeedParsedParse(
            context,
            "new.pdf",
            parsedAt: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

        var (items, total) = await CreateService(context).GetImportableListAsync(0, 200);

        total.Should().Be(2);
        items.Select(item => item.FileName).Should().Equal("new.pdf", "old.pdf");
    }

    [Fact]
    public async Task GetBlocksAsync_ParseNotFound_ThrowsKeyNotFoundException()
    {
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();

        var act = () => CreateService(context)
            .GetBlocksAsync(Guid.NewGuid(), null, null, 1, 50);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetBlocksAsync_ParseNotParsed_ThrowsInvalidOperationException()
    {
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        parse.Status = DocumentParseStatus.Parsing;
        context.SaveChanges();

        var act = () => CreateService(context)
            .GetBlocksAsync(parse.Id, null, null, 1, 50);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetBlocksAsync_AppliesPageAndBlockTypeFilters()
    {
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        QuestionBankTestData.AddBlock(context, parse.Id, 0, 0, "text", "page-zero");
        QuestionBankTestData.AddBlock(context, parse.Id, 0, 1, "image", "image");
        QuestionBankTestData.AddBlock(context, parse.Id, 1, 0, "text", "page-one");

        var (items, total) = await CreateService(context)
            .GetBlocksAsync(parse.Id, 0, "text", 1, 500);

        total.Should().Be(1);
        items.Should().ContainSingle();
        items[0].PageId.Should().Be(0);
        items[0].TextContent.Should().Be("page-zero");
    }

    [Fact]
    public async Task GetBlocksAsync_ImageBlockReturnsMetadataAndPresignedUrl()
    {
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        var image = QuestionBankTestData.AddImage(
            context,
            parse.Id,
            "figure.jpg",
            "images/figure.jpg");
        QuestionBankTestData.AddBlock(
            context,
            parse.Id,
            0,
            0,
            "image",
            imageId: image.Id);
        _ossMock
            .Setup(oss => oss.GetPresignedUrlAsync("images/figure.jpg", It.IsAny<int>()))
            .ReturnsAsync("https://oss.example/figure.jpg");

        var (items, _) = await CreateService(context)
            .GetBlocksAsync(parse.Id, null, null, 1, 50);

        items.Should().ContainSingle();
        items[0].ImageName.Should().Be("figure.jpg");
        items[0].ImagePath.Should().Be("images/figure.jpg");
        items[0].ImageUrl.Should().Be("https://oss.example/figure.jpg");
    }

    [Fact]
    public async Task GetBlocksAsync_PresignedUrlFailureReturnsNullUrl()
    {
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        var image = QuestionBankTestData.AddImage(
            context,
            parse.Id,
            "broken.jpg",
            "images/broken.jpg");
        QuestionBankTestData.AddBlock(
            context,
            parse.Id,
            0,
            0,
            "image",
            imageId: image.Id);
        _ossMock
            .Setup(oss => oss.GetPresignedUrlAsync("images/broken.jpg", It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("OSS unavailable"));

        var (items, _) = await CreateService(context)
            .GetBlocksAsync(parse.Id, null, null, 1, 50);

        items.Should().ContainSingle().Which.ImageUrl.Should().BeNull();
    }

    [Fact]
    public async Task GetBlocksAsync_ValidBlockDataReturnsJsonElement()
    {
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        QuestionBankTestData.AddBlock(
            context,
            parse.Id,
            0,
            0,
            "text",
            "hello",
            blockData: """{"type":"text","page_id":0}""");

        var (items, _) = await CreateService(context)
            .GetBlocksAsync(parse.Id, null, null, 1, 50);

        items[0].BlockData.Should().BeOfType<JsonElement>();
        ((JsonElement)items[0].BlockData!).GetProperty("type").GetString()
            .Should().Be("text");
    }

    [Fact]
    public async Task GetBlocksAsync_InvalidBlockDataReturnsNullWithoutLoggingPayload()
    {
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        QuestionBankTestData.AddBlock(
            context,
            parse.Id,
            0,
            0,
            "text",
            blockData: "{ sensitive invalid json");

        var (items, _) = await CreateService(context)
            .GetBlocksAsync(parse.Id, null, null, 1, 50);

        items[0].BlockData.Should().BeNull();
        _loggerMock.Verify(
            logger => logger.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    !state.ToString()!.Contains("sensitive invalid json")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task GetImageBlobAsync_ImageNotFound_ThrowsKeyNotFoundException()
    {
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();

        var act = () => CreateService(context).GetImageBlobAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetImageBlobAsync_OssFailurePropagates()
    {
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        var image = QuestionBankTestData.AddImage(
            context,
            parse.Id,
            "missing.jpg",
            "images/missing.jpg");
        _ossMock
            .Setup(oss => oss.DownloadAsync("images/missing.jpg"))
            .ThrowsAsync(new InvalidOperationException("OSS object not found"));

        var act = () => CreateService(context).GetImageBlobAsync(image.Id);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetImageBlobAsync_SuccessReturnsBlob()
    {
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        var image = QuestionBankTestData.AddImage(
            context,
            parse.Id,
            "image.png",
            "images/image.png",
            "image/png");
        var expectedBytes = new byte[] { 1, 2, 3, 4 };
        _ossMock
            .Setup(oss => oss.DownloadAsync("images/image.png"))
            .ReturnsAsync(new MemoryStream(expectedBytes));

        var blob = await CreateService(context).GetImageBlobAsync(image.Id);

        blob.ImageName.Should().Be("image.png");
        blob.ContentType.Should().Be("image/png");
        ((MemoryStream)blob.Stream).ToArray().Should().Equal(expectedBytes);
    }
}
