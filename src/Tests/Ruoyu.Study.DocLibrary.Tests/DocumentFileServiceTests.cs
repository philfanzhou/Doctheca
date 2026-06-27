using FluentAssertions;
using Moq;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

public class DocumentFileServiceTests
{
    private readonly Mock<IDocumentFileRepository> _fileRepoMock;
    private readonly Mock<ILogger<DocumentFileService>> _loggerMock;
    private readonly DocumentFileService _service;

    public DocumentFileServiceTests()
    {
        _fileRepoMock = new Mock<IDocumentFileRepository>();
        _loggerMock = new Mock<ILogger<DocumentFileService>>();
        _service = new DocumentFileService(_fileRepoMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task CreateAsync_SetsCreatedAtAndReturnsModel()
    {
        // Arrange
        var model = new DocumentFileModel
        {
            FileName = "test.pdf",
            FilePath = "docretrieval-files/abc.pdf",
            ContentType = "application/pdf",
        };

        _fileRepoMock.Setup(r => r.AddAsync(It.IsAny<DocumentFileModel>()))
            .Callback<DocumentFileModel>(m => m.Id = Guid.NewGuid())
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.CreateAsync(model);

        // Assert
        result.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        _fileRepoMock.Verify(r => r.AddAsync(It.Is<DocumentFileModel>(m =>
            m.FileName == "test.pdf")), Times.Once);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsModel_WhenExists()
    {
        // Arrange
        var id = Guid.NewGuid();
        var model = new DocumentFileModel { Id = id, FileName = "test.pdf" };
        _fileRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(model);

        // Act
        var result = await _service.GetByIdAsync(id);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(id);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotExists()
    {
        // Arrange
        var id = Guid.NewGuid();
        _fileRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync((DocumentFileModel?)null);

        // Act
        var result = await _service.GetByIdAsync(id);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetListAsync_ReturnsPagedResults()
    {
        // Arrange
        var items = new List<DocumentFileModel>
        {
            new() { Id = Guid.NewGuid(), FileName = "a.pdf" },
            new() { Id = Guid.NewGuid(), FileName = "b.pdf" },
        };
        _fileRepoMock.Setup(r => r.GetListAsync(1, 20))
            .ReturnsAsync((items, 2));

        // Act
        var (resultItems, totalCount) = await _service.GetListAsync(1, 20);

        // Assert
        resultItems.Should().HaveCount(2);
        totalCount.Should().Be(2);
    }

    [Fact]
    public async Task DeleteAsync_DelegatesToRepository()
    {
        // Arrange
        var id = Guid.NewGuid();
        _fileRepoMock.Setup(r => r.DeleteAsync(id)).ReturnsAsync(true);

        // Act
        var result = await _service.DeleteAsync(id);

        // Assert
        result.Should().BeTrue();
        _fileRepoMock.Verify(r => r.DeleteAsync(id), Times.Once);
    }
}

public class DocumentParseServiceTests
{
    private readonly Mock<IDocumentParseRepository> _parseRepoMock;
    private readonly Mock<IDocumentParseImageRepository> _imageRepoMock;
    private readonly Mock<ILogger<DocumentParseService>> _loggerMock;
    private readonly DocumentParseService _service;

    public DocumentParseServiceTests()
    {
        _parseRepoMock = new Mock<IDocumentParseRepository>();
        _imageRepoMock = new Mock<IDocumentParseImageRepository>();
        _loggerMock = new Mock<ILogger<DocumentParseService>>();
        _service = new DocumentParseService(_parseRepoMock.Object, _imageRepoMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task CreateAsync_SetsStatusToPendingAndReturnsModel()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        var model = new DocumentParseModel { DocumentFileId = fileId, Status = DocumentParseStatus.Pending };
        _parseRepoMock.Setup(r => r.AddAsync(It.IsAny<DocumentParseModel>()))
            .Callback<DocumentParseModel>(m => m.Id = Guid.NewGuid())
            .ReturnsAsync(model);

        // Act
        var result = await _service.CreateAsync(fileId);

        // Assert
        result.Status.Should().Be(DocumentParseStatus.Pending);
        result.DocumentFileId.Should().Be(fileId);
        _parseRepoMock.Verify(r => r.AddAsync(It.Is<DocumentParseModel>(m =>
            m.Status == DocumentParseStatus.Pending && m.DocumentFileId == fileId)), Times.Once);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsModel_WhenExists()
    {
        // Arrange
        var id = Guid.NewGuid();
        var model = new DocumentParseModel { Id = id };
        _parseRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(model);

        // Act
        var result = await _service.GetByIdAsync(id);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(id);
    }

    [Fact]
    public async Task GetLatestByFileIdAsync_ReturnsLatestParse()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        var model = new DocumentParseModel { Id = Guid.NewGuid(), DocumentFileId = fileId };
        _parseRepoMock.Setup(r => r.GetLatestByFileIdAsync(fileId)).ReturnsAsync(model);

        // Act
        var result = await _service.GetLatestByFileIdAsync(fileId);

        // Assert
        result.Should().NotBeNull();
        result!.DocumentFileId.Should().Be(fileId);
    }

    [Fact]
    public async Task UpdateStatusAsync_UpdatesStatusAndFields()
    {
        // Arrange
        var id = Guid.NewGuid();
        var model = new DocumentParseModel { Id = id, Status = DocumentParseStatus.Pending };
        _parseRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(model);
        _parseRepoMock.Setup(r => r.UpdateAsync(It.IsAny<DocumentParseModel>())).ReturnsAsync(model);

        // Act
        var result = await _service.UpdateStatusAsync(id, DocumentParseStatus.Parsing, externalTaskId: "task-123");

        // Assert
        result.Status.Should().Be(DocumentParseStatus.Parsing);
        result.ExternalTaskId.Should().Be("task-123");
        _parseRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentParseModel>(m =>
            m.Status == DocumentParseStatus.Parsing && m.ExternalTaskId == "task-123")), Times.Once);
    }

    [Fact]
    public async Task UpdateStatusAsync_SetsParsedAt_WhenStatusIsParsed()
    {
        // Arrange
        var id = Guid.NewGuid();
        var model = new DocumentParseModel { Id = id, Status = DocumentParseStatus.Parsing };
        _parseRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(model);
        _parseRepoMock.Setup(r => r.UpdateAsync(It.IsAny<DocumentParseModel>())).ReturnsAsync(model);

        // Act
        var result = await _service.UpdateStatusAsync(id, DocumentParseStatus.Parsed, markdownContent: "# Hello");

        // Assert
        result.ParsedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        result.MarkdownContent.Should().Be("# Hello");
    }

    [Fact]
    public async Task UpdateStatusAsync_ThrowsKeyNotFound_WhenNotExists()
    {
        // Arrange
        var id = Guid.NewGuid();
        _parseRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync((DocumentParseModel?)null);

        // Act
        var act = () => _service.UpdateStatusAsync(id, DocumentParseStatus.Parsing);

        // Assert
        await act.Should().ThrowAsync<KeyNotFoundException>().WithMessage($"*{id}*");
    }

    [Fact]
    public async Task UpdateStatusAsync_SetsErrorMessage_WhenProvided()
    {
        // Arrange
        var id = Guid.NewGuid();
        var model = new DocumentParseModel { Id = id, Status = DocumentParseStatus.Parsing };
        _parseRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(model);
        _parseRepoMock.Setup(r => r.UpdateAsync(It.IsAny<DocumentParseModel>())).ReturnsAsync(model);

        // Act
        var result = await _service.UpdateStatusAsync(id, DocumentParseStatus.Failed, errorMessage: "Parse error");

        // Assert
        result.ErrorMessage.Should().Be("Parse error");
    }

    [Fact]
    public async Task GetPendingJobsAsync_ReturnsOnlyPendingJobs()
    {
        // Arrange
        var items = new List<DocumentParseModel>
        {
            new() { Id = Guid.NewGuid(), Status = DocumentParseStatus.Pending },
        };
        _parseRepoMock.Setup(r => r.GetByStatusAsync(DocumentParseStatus.Pending))
            .ReturnsAsync(items);

        // Act
        var result = await _service.GetPendingJobsAsync();

        // Assert
        result.Should().HaveCount(1);
        result[0].Status.Should().Be(DocumentParseStatus.Pending);
    }

    [Fact]
    public async Task AddImageAsync_CallsRepository()
    {
        // Arrange
        var image = new DocumentParseImageModel
        {
            ParseId = Guid.NewGuid(),
            ImageName = "img1.jpg",
            ImagePath = "docretrieval-images/abc/img1.jpg",
        };
        _imageRepoMock.Setup(r => r.AddAsync(image)).ReturnsAsync(image);

        // Act
        await _service.AddImageAsync(image);

        // Assert
        _imageRepoMock.Verify(r => r.AddAsync(image), Times.Once);
    }

    [Fact]
    public async Task GetImagesByParseIdAsync_ReturnsImages()
    {
        // Arrange
        var parseId = Guid.NewGuid();
        var images = new List<DocumentParseImageModel>
        {
            new() { Id = Guid.NewGuid(), ParseId = parseId, ImageName = "img1.jpg" },
            new() { Id = Guid.NewGuid(), ParseId = parseId, ImageName = "img2.png" },
        };
        _imageRepoMock.Setup(r => r.GetByParseIdAsync(parseId)).ReturnsAsync(images);

        // Act
        var result = await _service.GetImagesByParseIdAsync(parseId);

        // Assert
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetImagesByFileIdAsync_ReturnsImages()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        var images = new List<DocumentParseImageModel>
        {
            new() { Id = Guid.NewGuid(), ParseId = Guid.NewGuid(), ImageName = "img1.jpg" },
        };
        _imageRepoMock.Setup(r => r.GetByFileIdAsync(fileId)).ReturnsAsync(images);

        // Act
        var result = await _service.GetImagesByFileIdAsync(fileId);

        // Assert
        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetListAsync_ReturnsPagedResults_WithSearch()
    {
        // Arrange
        var items = new List<DocumentParseModel>
        {
            new() { Id = Guid.NewGuid(), Status = DocumentParseStatus.Parsed },
            new() { Id = Guid.NewGuid(), Status = DocumentParseStatus.Failed },
        };
        _parseRepoMock.Setup(r => r.GetListAsync(1, 20, "test"))
            .ReturnsAsync((items, 2));

        // Act
        var (resultItems, totalCount) = await _service.GetListAsync(1, 20, "test");

        // Assert
        resultItems.Should().HaveCount(2);
        totalCount.Should().Be(2);
        _parseRepoMock.Verify(r => r.GetListAsync(1, 20, "test"), Times.Once);
    }

    [Fact]
    public async Task GetListAsync_ReturnsPagedResults_WithoutSearch()
    {
        // Arrange
        var items = new List<DocumentParseModel>
        {
            new() { Id = Guid.NewGuid(), Status = DocumentParseStatus.Parsed },
        };
        _parseRepoMock.Setup(r => r.GetListAsync(1, 20, null))
            .ReturnsAsync((items, 1));

        // Act
        var (resultItems, totalCount) = await _service.GetListAsync(1, 20);

        // Assert
        resultItems.Should().HaveCount(1);
        totalCount.Should().Be(1);
    }

    [Fact]
    public async Task DeleteParseAsync_DeletesImagesAndParse()
    {
        // Arrange
        var parseId = Guid.NewGuid();
        var model = new DocumentParseModel { Id = parseId, DocumentFileId = Guid.NewGuid() };
        _parseRepoMock.Setup(r => r.GetByIdAsync(parseId)).ReturnsAsync(model);
        _imageRepoMock.Setup(r => r.DeleteByParseIdAsync(parseId)).Returns(Task.CompletedTask);
        _parseRepoMock.Setup(r => r.DeleteAsync(parseId)).Returns(Task.CompletedTask);

        // Act
        var result = await _service.DeleteParseAsync(parseId);

        // Assert
        result.Should().BeTrue();
        _imageRepoMock.Verify(r => r.DeleteByParseIdAsync(parseId), Times.Once);
        _parseRepoMock.Verify(r => r.DeleteAsync(parseId), Times.Once);
    }

    [Fact]
    public async Task DeleteParseAsync_ReturnsFalse_WhenNotFound()
    {
        // Arrange
        var parseId = Guid.NewGuid();
        _parseRepoMock.Setup(r => r.GetByIdAsync(parseId)).ReturnsAsync((DocumentParseModel?)null);

        // Act
        var result = await _service.DeleteParseAsync(parseId);

        // Assert
        result.Should().BeFalse();
        _imageRepoMock.Verify(r => r.DeleteByParseIdAsync(It.IsAny<Guid>()), Times.Never);
        _parseRepoMock.Verify(r => r.DeleteAsync(It.IsAny<Guid>()), Times.Never);
    }
}
