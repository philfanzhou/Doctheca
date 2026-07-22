using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Database;
using Ruoyu.Study.DocLibrary.Database.Entities;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Service;
using Ruoyu.Study.DocLibrary.Tests.TestHelpers;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

public class QuestionBankImportServiceTests
{
    private readonly Mock<IDocumentParseImportRepository> _importRepoMock;
    private readonly Mock<IOssService> _ossMock;
    private readonly Mock<ILogger<QuestionBankImportService>> _loggerMock;

    public QuestionBankImportServiceTests()
    {
        _importRepoMock = new Mock<IDocumentParseImportRepository>();
        _ossMock = new Mock<IOssService>();
        _loggerMock = new Mock<ILogger<QuestionBankImportService>>();
    }

    private QuestionBankImportService CreateService(DocLibraryDbContext context)
    {
        return new QuestionBankImportService(
            context,
            _importRepoMock.Object,
            _ossMock.Object,
            _loggerMock.Object);
    }

    // ========== GetImportableListAsync ==========

    [Fact]
    public async Task GetImportableList_OnlyReturnsParsedStatus()
    {
        // UT-QBI-01: 仅返回 status=parsed 的 parse
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        QuestionBankTestData.SeedParsedParse(context, "parsed.pdf");
        var (parse2, _) = QuestionBankTestData.SeedParsedParse(context, "pending.pdf");
        parse2.Status = DocumentParseStatus.Pending;
        context.SaveChanges();

        var service = CreateService(context);
        var (items, total) = await service.GetImportableListAsync(1, 20);

        total.Should().Be(1);
        items.Should().HaveCount(1);
        items[0].FileName.Should().Be("parsed.pdf");
    }

    [Fact]
    public async Task GetImportableList_SearchMatchesFileName()
    {
        // UT-QBI-02: search 模糊匹配 file_name
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        QuestionBankTestData.SeedParsedParse(context, "2024-英语-期末.pdf");
        QuestionBankTestData.SeedParsedParse(context, "2024-数学-月考.pdf");

        var service = CreateService(context);
        var (items, total) = await service.GetImportableListAsync(1, 20, search: "期末");

        total.Should().Be(1);
        items.Should().HaveCount(1);
        items[0].FileName.Should().Contain("期末");
    }

    [Fact]
    public async Task GetImportableList_ExcludesImportedByDefault()
    {
        // UT-QBI-03: includeImported=false 排除 imported
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parseA, _) = QuestionBankTestData.SeedParsedParse(context, "a.pdf");
        var (parseB, _) = QuestionBankTestData.SeedParsedParse(context, "b.pdf");

        _importRepoMock
            .Setup(r => r.GetByParseIdAsync(parseB.Id))
            .ReturnsAsync(new DocumentParseImportModel
            {
                ParseId = parseB.Id,
                Status = ParseImportStatus.Imported,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        // Note: GetImportableListAsync uses DbContext join, not repository. Need to seed import record directly.
        context.DocumentParseImports.Add(new DocumentParseImportEntity
        {
            ParseId = parseB.Id,
            ImportedBy = Guid.NewGuid(),
            Status = ParseImportStatus.Imported,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        context.SaveChanges();

        var service = CreateService(context);
        var (items, total) = await service.GetImportableListAsync(1, 20, includeImported: false);

        total.Should().Be(1);
        items.Should().HaveCount(1);
        items[0].ParseId.Should().Be(parseA.Id);
    }

    [Fact]
    public async Task GetImportableList_IncludesImportedWhenFlagTrue()
    {
        // UT-QBI-04: includeImported=true 包含 imported
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parseA, _) = QuestionBankTestData.SeedParsedParse(context, "a.pdf");
        var (parseB, _) = QuestionBankTestData.SeedParsedParse(context, "b.pdf");

        var importedAt = DateTimeOffset.UtcNow;
        context.DocumentParseImports.Add(new DocumentParseImportEntity
        {
            ParseId = parseB.Id,
            ImportedBy = Guid.NewGuid(),
            Status = ParseImportStatus.Imported,
            CreatedAt = importedAt,
        });
        context.SaveChanges();

        var service = CreateService(context);
        var (items, total) = await service.GetImportableListAsync(1, 20, includeImported: true);

        total.Should().Be(2);
        var itemB = items.First(i => i.ParseId == parseB.Id);
        itemB.ImportStatus.Should().Be(ParseImportStatus.Imported);
        itemB.ImportedAt.Should().NotBeNull();
        var itemA = items.First(i => i.ParseId == parseA.Id);
        itemA.ImportStatus.Should().BeNull();
        itemA.ImportedAt.Should().BeNull();
    }

    [Fact]
    public async Task GetImportableList_FailedStatusNotExcluded()
    {
        // UT-QBI-05: failed 状态不被排除
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parseC, _) = QuestionBankTestData.SeedParsedParse(context, "c.pdf");

        context.DocumentParseImports.Add(new DocumentParseImportEntity
        {
            ParseId = parseC.Id,
            ImportedBy = Guid.NewGuid(),
            Status = ParseImportStatus.Failed,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        context.SaveChanges();

        var service = CreateService(context);
        var (items, total) = await service.GetImportableListAsync(1, 20, includeImported: false);

        total.Should().Be(1);
        items[0].ParseId.Should().Be(parseC.Id);
        items[0].ImportStatus.Should().Be(ParseImportStatus.Failed);
    }

    [Fact]
    public async Task GetImportableList_PageSizeCappedToMax()
    {
        // UT-QBI-06: 分页参数修正(pageSize > 100)
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        QuestionBankTestData.SeedParsedParse(context, "test.pdf");

        var service = CreateService(context);
        var (items, total) = await service.GetImportableListAsync(0, 200);

        total.Should().Be(1);
        items.Should().HaveCount(1);
        // Service corrects internally; verify no exception and results returned
    }

    [Fact]
    public async Task GetImportableList_OrderedByParsedAtDesc()
    {
        // UT-QBI-07: 按 parsed_at DESC 排序
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var t1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        QuestionBankTestData.SeedParsedParse(context, "oldest.pdf", parsedAt: t1);
        QuestionBankTestData.SeedParsedParse(context, "newest.pdf", parsedAt: t3);
        QuestionBankTestData.SeedParsedParse(context, "middle.pdf", parsedAt: t2);

        var service = CreateService(context);
        var (items, total) = await service.GetImportableListAsync(1, 20);

        total.Should().Be(3);
        items[0].FileName.Should().Be("newest.pdf");
        items[1].FileName.Should().Be("middle.pdf");
        items[2].FileName.Should().Be("oldest.pdf");
    }

    // ========== GetBlocksAsync ==========

    [Fact]
    public async Task GetBlocks_ParseNotFound_ThrowsKeyNotFoundException()
    {
        // UT-QBI-08: parse 不存在抛 KeyNotFoundException
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var service = CreateService(context);

        var act = () => service.GetBlocksAsync(Guid.NewGuid(), null, null, 1, 50);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetBlocks_ParseNotParsed_ThrowsInvalidOperationException()
    {
        // UT-QBI-09: parse 状态非 parsed 抛 InvalidOperationException
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        parse.Status = DocumentParseStatus.Parsing;
        context.SaveChanges();

        var service = CreateService(context);

        var act = () => service.GetBlocksAsync(parse.Id, null, null, 1, 50);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetBlocks_PageIdZeroFilterWorks()
    {
        // UT-QBI-10: pageId=0 过滤有效(0 是有效值)
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        QuestionBankTestData.AddBlock(context, parse.Id, 0, 0, "text", "page0");
        QuestionBankTestData.AddBlock(context, parse.Id, 1, 0, "text", "page1");
        QuestionBankTestData.AddBlock(context, parse.Id, 2, 0, "text", "page2");

        var service = CreateService(context);
        var (items, total) = await service.GetBlocksAsync(parse.Id, 0, null, 1, 50);

        total.Should().Be(1);
        items.Should().HaveCount(1);
        items[0].PageId.Should().Be(0);
        items[0].TextContent.Should().Be("page0");
    }

    [Fact]
    public async Task GetBlocks_BlockTypeFilterWorks()
    {
        // UT-QBI-11: blockType 过滤
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        QuestionBankTestData.AddBlock(context, parse.Id, 0, 0, "text", "t1");
        QuestionBankTestData.AddBlock(context, parse.Id, 0, 1, "image", "img1");
        QuestionBankTestData.AddBlock(context, parse.Id, 0, 2, "table", "tab1");

        var service = CreateService(context);
        var (items, total) = await service.GetBlocksAsync(parse.Id, null, "image", 1, 50);

        total.Should().Be(1);
        items.Should().HaveCount(1);
        items[0].BlockType.Should().Be("image");
    }

    [Fact]
    public async Task GetBlocks_ImageBlockReturnsImageFields()
    {
        // UT-QBI-12: image block 返回 imageName/imagePath/imageUrl
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        var image = QuestionBankTestData.AddImage(context, parse.Id, "figure1.jpg", "images/figure1.jpg");
        QuestionBankTestData.AddBlock(context, parse.Id, 0, 0, "image", imageId: image.Id);

        _ossMock
            .Setup(o => o.GetPresignedUrlAsync("images/figure1.jpg", It.IsAny<int>()))
            .ReturnsAsync("https://oss.example.com/presigned/figure1.jpg");

        var service = CreateService(context);
        var (items, total) = await service.GetBlocksAsync(parse.Id, null, null, 1, 50);

        total.Should().Be(1);
        items[0].ImageName.Should().Be("figure1.jpg");
        items[0].ImagePath.Should().Be("images/figure1.jpg");
        items[0].ImageUrl.Should().Be("https://oss.example.com/presigned/figure1.jpg");
    }

    [Fact]
    public async Task GetBlocks_TextBlockHasNullImageFields()
    {
        // UT-QBI-13: text block 的图片字段为 null
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        QuestionBankTestData.AddBlock(context, parse.Id, 0, 0, "text", "hello");

        var service = CreateService(context);
        var (items, total) = await service.GetBlocksAsync(parse.Id, null, null, 1, 50);

        total.Should().Be(1);
        items[0].ImageName.Should().BeNull();
        items[0].ImagePath.Should().BeNull();
        items[0].ImageUrl.Should().BeNull();
    }

    [Fact]
    public async Task GetBlocks_ImageIdSetButImageMissing_ReturnsNullFields()
    {
        // UT-QBI-14: image_id 有值但 image 记录缺失
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        // Add block with image_id pointing to non-existent image
        var phantomImageId = Guid.NewGuid();
        QuestionBankTestData.AddBlock(context, parse.Id, 0, 0, "image", imageId: phantomImageId);

        var service = CreateService(context);
        var (items, total) = await service.GetBlocksAsync(parse.Id, null, null, 1, 50);

        total.Should().Be(1);
        items[0].ImageName.Should().BeNull();
        items[0].ImagePath.Should().BeNull();
        items[0].ImageUrl.Should().BeNull();
    }

    [Fact]
    public async Task GetBlocks_OssPresignedUrlFailure_ReturnsNullUrl()
    {
        // UT-QBI-15: OSS 生成 presigned URL 失败时 imageUrl=null
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        var image = QuestionBankTestData.AddImage(context, parse.Id, "broken.jpg", "images/broken.jpg");
        QuestionBankTestData.AddBlock(context, parse.Id, 0, 0, "image", imageId: image.Id);

        _ossMock
            .Setup(o => o.GetPresignedUrlAsync("images/broken.jpg", It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("OSS connection failed"));

        var service = CreateService(context);
        var (items, total) = await service.GetBlocksAsync(parse.Id, null, null, 1, 50);

        total.Should().Be(1);
        items[0].ImageName.Should().Be("broken.jpg");
        items[0].ImagePath.Should().Be("images/broken.jpg");
        items[0].ImageUrl.Should().BeNull();
    }

    [Fact]
    public async Task GetBlocks_PageSizeCappedToMax()
    {
        // UT-QBI-16: 分页参数修正(pageSize > 200)
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        QuestionBankTestData.AddBlock(context, parse.Id, 0, 0, "text", "b1");
        QuestionBankTestData.AddBlock(context, parse.Id, 0, 1, "text", "b2");

        var service = CreateService(context);
        var (items, total) = await service.GetBlocksAsync(parse.Id, null, null, 1, 500);

        total.Should().Be(2);
        items.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetBlocks_BlockDataValidJson_ReturnsObject()
    {
        // UT-QBI-17: blockData 合法 JSON 解析为对象
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        QuestionBankTestData.AddBlock(context, parse.Id, 0, 0, "text", "hello",
            blockData: "{\"type\":\"text\",\"text\":\"hello\",\"page_id\":0}");

        var service = CreateService(context);
        var (items, _) = await service.GetBlocksAsync(parse.Id, null, null, 1, 50);

        items[0].BlockData.Should().NotBeNull();
        items[0].BlockData.Should().BeOfType<JsonElement>();
        var element = (JsonElement)items[0].BlockData!;
        element.GetProperty("type").GetString().Should().Be("text");
        element.GetProperty("text").GetString().Should().Be("hello");
        element.GetProperty("page_id").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task GetBlocks_BlockDataInvalidJson_ReturnsNull()
    {
        // UT-QBI-18: blockData 非法 JSON 返回 null
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        QuestionBankTestData.AddBlock(context, parse.Id, 0, 0, "text", "hello", blockData: "{ invalid json");

        var service = CreateService(context);
        var (items, _) = await service.GetBlocksAsync(parse.Id, null, null, 1, 50);

        items[0].BlockData.Should().BeNull();
    }

    // ========== GetImageBlobAsync ==========

    [Fact]
    public async Task GetImageBlob_ImageNotFound_ThrowsKeyNotFoundException()
    {
        // UT-QBI-19: imageId 不存在抛 KeyNotFoundException
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var service = CreateService(context);

        var act = () => service.GetImageBlobAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetImageBlob_OssDownloadFailure_Throws()
    {
        // UT-QBI-20: OSS 下载失败抛异常
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        var image = QuestionBankTestData.AddImage(context, parse.Id, "lost.jpg", "images/lost.jpg");

        _ossMock
            .Setup(o => o.DownloadAsync("images/lost.jpg"))
            .ThrowsAsync(new InvalidOperationException("OSS object not found"));

        var service = CreateService(context);

        var act = () => service.GetImageBlobAsync(image.Id);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetImageBlob_Success_ReturnsBlob()
    {
        // UT-QBI-21: 成功返回 ParseImageBlob
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        var image = QuestionBankTestData.AddImage(context, parse.Id, "ok.png", "images/ok.png", "image/png");

        var expectedBytes = new byte[] { 1, 2, 3, 4 };
        _ossMock
            .Setup(o => o.DownloadAsync("images/ok.png"))
            .ReturnsAsync(new MemoryStream(expectedBytes));

        var service = CreateService(context);
        var blob = await service.GetImageBlobAsync(image.Id);

        blob.ImageName.Should().Be("ok.png");
        blob.ContentType.Should().Be("image/png");
        blob.Stream.Should().NotBeNull();
        using var reader = new StreamReader(blob.Stream);
        var streamBytes = ((MemoryStream)blob.Stream).ToArray();
        streamBytes.Should().Equal(expectedBytes);
    }

    // ========== UpsertImportStatusAsync ==========

    [Fact]
    public async Task UpsertImportStatus_ParseNotFound_ThrowsKeyNotFoundException()
    {
        // UT-QBI-22: parse 不存在抛 KeyNotFoundException
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var service = CreateService(context);

        var act = () => service.UpsertImportStatusAsync(
            Guid.NewGuid(), Guid.NewGuid(), ParseImportStatus.Imported, null, null);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task UpsertImportStatus_InvalidStatus_ThrowsArgumentException()
    {
        // UT-QBI-23: status 非法抛 ArgumentException
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        var service = CreateService(context);

        var act = () => service.UpsertImportStatusAsync(
            parse.Id, Guid.NewGuid(), "invalid", null, null);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UpsertImportStatus_FirstTimeImported_CallsAddAsync()
    {
        // UT-QBI-24: 首次插入 imported
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        var importedBy = Guid.NewGuid();

        _importRepoMock
            .Setup(r => r.GetByParseIdAsync(parse.Id))
            .ReturnsAsync((DocumentParseImportModel?)null);

        DocumentParseImportModel? captured = null;
        _importRepoMock
            .Setup(r => r.AddAsync(It.IsAny<DocumentParseImportModel>()))
            .Callback<DocumentParseImportModel>(m => captured = m)
            .ReturnsAsync((DocumentParseImportModel m) => { m.Id = Guid.NewGuid(); return m; });

        var service = CreateService(context);
        var result = await service.UpsertImportStatusAsync(
            parse.Id, importedBy, ParseImportStatus.Imported, "Imported 50 questions", "[\"q1\",\"q2\"]");

        result.ParseId.Should().Be(parse.Id);
        result.ImportStatus.Should().Be(ParseImportStatus.Imported);
        result.UpdatedAt.Should().BeNull();

        captured.Should().NotBeNull();
        captured!.ParseId.Should().Be(parse.Id);
        captured.ImportedBy.Should().Be(importedBy);
        captured.Status.Should().Be(ParseImportStatus.Imported);
        captured.Note.Should().Be("Imported 50 questions");
        captured.ImportedQuestionIds.Should().Be("[\"q1\",\"q2\"]");

        _importRepoMock.Verify(r => r.UpdateAsync(It.IsAny<DocumentParseImportModel>()), Times.Never);
    }

    [Fact]
    public async Task UpsertImportStatus_FirstTimeFailed_CallsAddAsync()
    {
        // UT-QBI-25: 首次插入 failed
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);

        _importRepoMock
            .Setup(r => r.GetByParseIdAsync(parse.Id))
            .ReturnsAsync((DocumentParseImportModel?)null);
        _importRepoMock
            .Setup(r => r.AddAsync(It.IsAny<DocumentParseImportModel>()))
            .ReturnsAsync((DocumentParseImportModel m) => { m.Id = Guid.NewGuid(); return m; });

        var service = CreateService(context);
        var result = await service.UpsertImportStatusAsync(
            parse.Id, Guid.NewGuid(), ParseImportStatus.Failed, "Validation error", null);

        result.ImportStatus.Should().Be(ParseImportStatus.Failed);
        _importRepoMock.Verify(r => r.AddAsync(It.IsAny<DocumentParseImportModel>()), Times.Once);
    }

    [Fact]
    public async Task UpsertImportStatus_ImportedToImported_ThrowsInvalidOperationException()
    {
        // UT-QBI-26: imported → imported 抛异常
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);

        _importRepoMock
            .Setup(r => r.GetByParseIdAsync(parse.Id))
            .ReturnsAsync(new DocumentParseImportModel
            {
                Id = Guid.NewGuid(),
                ParseId = parse.Id,
                Status = ParseImportStatus.Imported,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        var service = CreateService(context);

        var act = () => service.UpsertImportStatusAsync(
            parse.Id, Guid.NewGuid(), ParseImportStatus.Imported, null, null);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _importRepoMock.Verify(r => r.UpdateAsync(It.IsAny<DocumentParseImportModel>()), Times.Never);
    }

    [Fact]
    public async Task UpsertImportStatus_ImportedToFailed_CallsUpdateAsync()
    {
        // UT-QBI-27: imported → failed 成功覆盖
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);
        var existingCreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var existing = new DocumentParseImportModel
        {
            Id = Guid.NewGuid(),
            ParseId = parse.Id,
            Status = ParseImportStatus.Imported,
            CreatedAt = existingCreatedAt,
        };
        _importRepoMock
            .Setup(r => r.GetByParseIdAsync(parse.Id))
            .ReturnsAsync(existing);

        DocumentParseImportModel? updated = null;
        _importRepoMock
            .Setup(r => r.UpdateAsync(It.IsAny<DocumentParseImportModel>()))
            .Callback<DocumentParseImportModel>(m => updated = m)
            .Returns(Task.CompletedTask);

        var service = CreateService(context);
        var result = await service.UpsertImportStatusAsync(
            parse.Id, Guid.NewGuid(), ParseImportStatus.Failed, "Re-import failed", null);

        result.ImportStatus.Should().Be(ParseImportStatus.Failed);
        result.ImportedAt.Should().Be(existingCreatedAt);
        result.UpdatedAt.Should().NotBeNull();

        updated.Should().NotBeNull();
        updated!.Status.Should().Be(ParseImportStatus.Failed);
        updated.Note.Should().Be("Re-import failed");
        _importRepoMock.Verify(r => r.AddAsync(It.IsAny<DocumentParseImportModel>()), Times.Never);
    }

    [Fact]
    public async Task UpsertImportStatus_FailedToImported_CallsUpdateAsync()
    {
        // UT-QBI-28: failed → imported 成功覆盖
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);

        _importRepoMock
            .Setup(r => r.GetByParseIdAsync(parse.Id))
            .ReturnsAsync(new DocumentParseImportModel
            {
                Id = Guid.NewGuid(),
                ParseId = parse.Id,
                Status = ParseImportStatus.Failed,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        var service = CreateService(context);
        var result = await service.UpsertImportStatusAsync(
            parse.Id, Guid.NewGuid(), ParseImportStatus.Imported, "Re-imported successfully", null);

        result.ImportStatus.Should().Be(ParseImportStatus.Imported);
        _importRepoMock.Verify(r => r.UpdateAsync(It.IsAny<DocumentParseImportModel>()), Times.Once);
    }

    [Fact]
    public async Task UpsertImportStatus_FailedToFailed_CallsUpdateAsync()
    {
        // UT-QBI-29: failed → failed 成功覆盖(允许重复 failed)
        using var context = InMemoryDbContextFactory.CreateInMemoryContext();
        var (parse, _) = QuestionBankTestData.SeedParsedParse(context);

        _importRepoMock
            .Setup(r => r.GetByParseIdAsync(parse.Id))
            .ReturnsAsync(new DocumentParseImportModel
            {
                Id = Guid.NewGuid(),
                ParseId = parse.Id,
                Status = ParseImportStatus.Failed,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        var service = CreateService(context);
        var result = await service.UpsertImportStatusAsync(
            parse.Id, Guid.NewGuid(), ParseImportStatus.Failed, "Still failing", null);

        result.ImportStatus.Should().Be(ParseImportStatus.Failed);
        _importRepoMock.Verify(r => r.UpdateAsync(It.IsAny<DocumentParseImportModel>()), Times.Once);
    }
}
