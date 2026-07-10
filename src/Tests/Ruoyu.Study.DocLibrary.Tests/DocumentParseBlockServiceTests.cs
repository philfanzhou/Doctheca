using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

public class DocumentParseBlockServiceTests
{
    private readonly Mock<IDocumentParseBlockRepository> _blockRepoMock;
    private readonly DocumentParseBlockService _service;

    public DocumentParseBlockServiceTests()
    {
        _blockRepoMock = new Mock<IDocumentParseBlockRepository>();
        var logger = new Mock<ILogger<DocumentParseBlockService>>();
        _service = new DocumentParseBlockService(_blockRepoMock.Object, logger.Object);
    }

    [Fact]
    public async Task InsertBlocksFromContentListAsync_WithValidJson_InsertsAllBlocks()
    {
        // Arrange
        var parseId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        var imageMap = new Dictionary<string, Guid> { ["pic1.jpg"] = imageId };

        var contentListJson = @"[
            { ""type"": ""text"", ""page_id"": 0, ""text"": ""Hello world"" },
            { ""type"": ""image"", ""page_id"": 0, ""img_path"": ""images/pic1.jpg"" },
            { ""type"": ""equation"", ""page_id"": 1, ""text"": ""E=mc^2"" }
        ]";

        List<DocumentParseBlockModel>? captured = null;
        _blockRepoMock.Setup(r => r.AddBlocksAsync(parseId, It.IsAny<IEnumerable<DocumentParseBlockModel>>()))
            .Callback<Guid, IEnumerable<DocumentParseBlockModel>>((_, blocks) => captured = blocks.ToList())
            .Returns(Task.CompletedTask);

        // Act
        await _service.InsertBlocksFromContentListAsync(parseId, contentListJson, imageMap);

        // Assert
        captured.Should().NotBeNull();
        captured!.Should().HaveCount(3);
        captured[0].BlockType.Should().Be("text");
        captured[0].TextContent.Should().Be("Hello world");
        captured[0].PageId.Should().Be(0);
        captured[1].BlockType.Should().Be("image");
        captured[1].ImageId.Should().Be(imageId);
        captured[2].BlockType.Should().Be("equation");
        captured[2].TextContent.Should().Be("E=mc^2");
    }

    [Fact]
    public async Task InsertBlocksFromContentListAsync_AssignsSortIndexPerPage()
    {
        // Arrange
        var parseId = Guid.NewGuid();
        var contentListJson = @"[
            { ""type"": ""text"", ""page_id"": 0, ""text"": ""page 0 block 1"" },
            { ""type"": ""text"", ""page_id"": 1, ""text"": ""page 1 block 1"" },
            { ""type"": ""text"", ""page_id"": 0, ""text"": ""page 0 block 2"" },
            { ""type"": ""text"", ""page_id"": 1, ""text"": ""page 1 block 2"" }
        ]";

        List<DocumentParseBlockModel>? captured = null;
        _blockRepoMock.Setup(r => r.AddBlocksAsync(parseId, It.IsAny<IEnumerable<DocumentParseBlockModel>>()))
            .Callback<Guid, IEnumerable<DocumentParseBlockModel>>((_, blocks) => captured = blocks.ToList())
            .Returns(Task.CompletedTask);

        // Act
        await _service.InsertBlocksFromContentListAsync(parseId, contentListJson, null);

        // Assert
        captured.Should().NotBeNull();
        var page0 = captured!.Where(b => b.PageId == 0).ToList();
        var page1 = captured.Where(b => b.PageId == 1).ToList();
        page0.Should().HaveCount(2);
        page1.Should().HaveCount(2);
        page0[0].SortIndex.Should().Be(0);
        page0[1].SortIndex.Should().Be(1);
        page1[0].SortIndex.Should().Be(0);
        page1[1].SortIndex.Should().Be(1);
    }

    [Fact]
    public async Task InsertBlocksFromContentListAsync_WithoutImageMap_LeavesImageIdNull()
    {
        // Arrange
        var parseId = Guid.NewGuid();
        var contentListJson = @"[
            { ""type"": ""image"", ""page_id"": 0, ""img_path"": ""images/pic1.jpg"" }
        ]";

        List<DocumentParseBlockModel>? captured = null;
        _blockRepoMock.Setup(r => r.AddBlocksAsync(parseId, It.IsAny<IEnumerable<DocumentParseBlockModel>>()))
            .Callback<Guid, IEnumerable<DocumentParseBlockModel>>((_, blocks) => captured = blocks.ToList())
            .Returns(Task.CompletedTask);

        // Act
        await _service.InsertBlocksFromContentListAsync(parseId, contentListJson, null);

        // Assert
        captured.Should().NotBeNull();
        captured![0].ImageId.Should().BeNull();
    }

    [Fact]
    public async Task InsertBlocksFromContentListAsync_PreservesRawBlockData()
    {
        // Arrange
        var parseId = Guid.NewGuid();
        var contentListJson = @"[
            { ""type"": ""text"", ""page_id"": 0, ""text"": ""hello"", ""angle"": 5, ""extra_field"": ""future_proof"" }
        ]";

        List<DocumentParseBlockModel>? captured = null;
        _blockRepoMock.Setup(r => r.AddBlocksAsync(parseId, It.IsAny<IEnumerable<DocumentParseBlockModel>>()))
            .Callback<Guid, IEnumerable<DocumentParseBlockModel>>((_, blocks) => captured = blocks.ToList())
            .Returns(Task.CompletedTask);

        // Act
        await _service.InsertBlocksFromContentListAsync(parseId, contentListJson, null);

        // Assert
        captured![0].BlockData.Should().Contain("extra_field");
        captured[0].BlockData.Should().Contain("future_proof");
    }

    [Fact]
    public async Task InsertBlocksFromContentListAsync_WithEmptyJson_DoesNotCallRepo()
    {
        // Act
        await _service.InsertBlocksFromContentListAsync(Guid.NewGuid(), "[]", null);

        // Assert
        _blockRepoMock.Verify(r => r.AddBlocksAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<DocumentParseBlockModel>>()),
            Times.Never);
    }

    [Fact]
    public async Task InsertBlocksFromContentListAsync_WithEmptyString_DoesNotCallRepo()
    {
        // Act
        await _service.InsertBlocksFromContentListAsync(Guid.NewGuid(), "", null);

        // Assert
        _blockRepoMock.Verify(r => r.AddBlocksAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<DocumentParseBlockModel>>()),
            Times.Never);
    }

    [Fact]
    public async Task InsertBlocksFromContentListAsync_WithInvalidJson_DoesNotCallRepo()
    {
        // Act
        await _service.InsertBlocksFromContentListAsync(Guid.NewGuid(), "{ invalid json", null);

        // Assert
        _blockRepoMock.Verify(r => r.AddBlocksAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<DocumentParseBlockModel>>()),
            Times.Never);
    }

    [Fact]
    public async Task InsertBlocksFromContentListAsync_DefaultsPageIdToZero_WhenMissing()
    {
        // Arrange
        var parseId = Guid.NewGuid();
        var contentListJson = @"[
            { ""type"": ""text"", ""text"": ""no page_id"" }
        ]";

        List<DocumentParseBlockModel>? captured = null;
        _blockRepoMock.Setup(r => r.AddBlocksAsync(parseId, It.IsAny<IEnumerable<DocumentParseBlockModel>>()))
            .Callback<Guid, IEnumerable<DocumentParseBlockModel>>((_, blocks) => captured = blocks.ToList())
            .Returns(Task.CompletedTask);

        // Act
        await _service.InsertBlocksFromContentListAsync(parseId, contentListJson, null);

        // Assert
        captured![0].PageId.Should().Be(0);
    }

    [Fact]
    public async Task InsertBlocksFromContentListAsync_ExtractsTextFromContentOrBodyField()
    {
        // Arrange - some blocks use "body" (e.g., table HTML) instead of "text"
        var parseId = Guid.NewGuid();
        var contentListJson = @"[
            { ""type"": ""table"", ""page_id"": 0, ""body"": ""<table><tr><td>x</td></tr></table>"" }
        ]";

        List<DocumentParseBlockModel>? captured = null;
        _blockRepoMock.Setup(r => r.AddBlocksAsync(parseId, It.IsAny<IEnumerable<DocumentParseBlockModel>>()))
            .Callback<Guid, IEnumerable<DocumentParseBlockModel>>((_, blocks) => captured = blocks.ToList())
            .Returns(Task.CompletedTask);

        // Act
        await _service.InsertBlocksFromContentListAsync(parseId, contentListJson, null);

        // Assert
        captured![0].TextContent.Should().Contain("<table>");
    }

    // ==================== [Gen-2] minerU field extraction ====================

    [Fact]
    public async Task InsertBlocksFromContentListAsync_ExtractsMinerUFields_PipelineBbox()
    {
        // Arrange - pipeline backend: bbox in 0-1000, has score, sub_type, text_level
        var parseId = Guid.NewGuid();
        var contentListJson = @"[
            {
                ""type"": ""text"",
                ""page_id"": 0,
                ""text"": ""heading"",
                ""sub_type"": ""text"",
                ""text_level"": 1,
                ""bbox"": [100, 200, 300, 400],
                ""score"": 0.95,
                ""image_caption"": [""Figure 1: diagram""]
            }
        ]";

        List<DocumentParseBlockModel>? captured = null;
        _blockRepoMock.Setup(r => r.AddBlocksAsync(parseId, It.IsAny<IEnumerable<DocumentParseBlockModel>>()))
            .Callback<Guid, IEnumerable<DocumentParseBlockModel>>((_, blocks) => captured = blocks.ToList())
            .Returns(Task.CompletedTask);

        // Act
        await _service.InsertBlocksFromContentListAsync(parseId, contentListJson, null);

        // Assert
        captured.Should().NotBeNull();
        captured![0].SubType.Should().Be("text");
        captured[0].TextLevel.Should().Be(1);
        captured[0].BboxX0.Should().Be(100f);
        captured[0].BboxY0.Should().Be(200f);
        captured[0].BboxX1.Should().Be(300f);
        captured[0].BboxY1.Should().Be(400f);
        captured[0].MineruScore.Should().Be(0.95);
        captured[0].Caption.Should().Be("Figure 1: diagram");
        // text_format absent in pipeline → empty string
        captured[0].TextFormat.Should().BeEmpty();
    }

    [Fact]
    public async Task InsertBlocksFromContentListAsync_NormalizesVlmBboxTo1000()
    {
        // Arrange - VLM backend: bbox in 0-1 percentage, should be normalized to 0-1000
        var parseId = Guid.NewGuid();
        var contentListJson = @"[
            {
                ""type"": ""image"",
                ""page_id"": 0,
                ""img_path"": ""images/img1.jpg"",
                ""bbox"": [0.1, 0.2, 0.3, 0.4],
                ""text_format"": ""latex""
            }
        ]";

        List<DocumentParseBlockModel>? captured = null;
        _blockRepoMock.Setup(r => r.AddBlocksAsync(parseId, It.IsAny<IEnumerable<DocumentParseBlockModel>>()))
            .Callback<Guid, IEnumerable<DocumentParseBlockModel>>((_, blocks) => captured = blocks.ToList())
            .Returns(Task.CompletedTask);

        // Act
        await _service.InsertBlocksFromContentListAsync(parseId, contentListJson, null);

        // Assert — VLM 0-1 values multiplied by 1000
        captured.Should().NotBeNull();
        captured![0].BboxX0.Should().Be(100f);
        captured[0].BboxY0.Should().Be(200f);
        captured[0].BboxX1.Should().Be(300f);
        captured[0].BboxY1.Should().Be(400f);
        captured[0].TextFormat.Should().Be("latex");
        // text_level absent → -1
        captured[0].TextLevel.Should().Be(-1);
        // score absent → null
        captured[0].MineruScore.Should().BeNull();
        // caption absent → null
        captured[0].Caption.Should().BeNull();
    }

    [Fact]
    public async Task InsertBlocksFromContentListAsync_NoMinerUFields_DefaultsApplied()
    {
        // Arrange - minimal block with only V1 fields, no minerU fields
        var parseId = Guid.NewGuid();
        var contentListJson = @"[
            { ""type"": ""text"", ""page_id"": 0, ""text"": ""plain text"" }
        ]";

        List<DocumentParseBlockModel>? captured = null;
        _blockRepoMock.Setup(r => r.AddBlocksAsync(parseId, It.IsAny<IEnumerable<DocumentParseBlockModel>>()))
            .Callback<Guid, IEnumerable<DocumentParseBlockModel>>((_, blocks) => captured = blocks.ToList())
            .Returns(Task.CompletedTask);

        // Act
        await _service.InsertBlocksFromContentListAsync(parseId, contentListJson, null);

        // Assert — minerU fields default to null/empty/-1
        captured.Should().NotBeNull();
        captured![0].SubType.Should().BeNull();
        captured[0].TextLevel.Should().Be(-1);
        captured[0].TextFormat.Should().BeEmpty();
        captured[0].BboxX0.Should().BeNull();
        captured[0].BboxY0.Should().BeNull();
        captured[0].BboxX1.Should().BeNull();
        captured[0].BboxY1.Should().BeNull();
        captured[0].MineruScore.Should().BeNull();
        captured[0].Caption.Should().BeNull();
    }
}
