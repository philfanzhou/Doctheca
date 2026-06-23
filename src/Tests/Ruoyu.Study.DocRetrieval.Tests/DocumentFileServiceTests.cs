using FluentAssertions;
using Moq;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ruoyu.Study.DocRetrieval.Tests;

public class DocumentFileServiceTests
{
    private readonly Mock<IDocumentFileRepository> _fileRepoMock;
    private readonly Mock<IDocumentFileImageRepository> _imageRepoMock;
    private readonly Mock<ILogger<DocumentFileService>> _loggerMock;
    private readonly DocumentFileService _service;

    public DocumentFileServiceTests()
    {
        _fileRepoMock = new Mock<IDocumentFileRepository>();
        _imageRepoMock = new Mock<IDocumentFileImageRepository>();
        _loggerMock = new Mock<ILogger<DocumentFileService>>();
        _service = new DocumentFileService(_fileRepoMock.Object, _imageRepoMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task CreateAsync_SetsStatusToUploadedAndReturnsModel()
    {
        // Arrange
        var model = new DocumentFileModel
        {
            FileName = "test.pdf",
            FilePath = "docretrieval-files/abc.pdf",
            FileSize = 1024,
            ContentType = "application/pdf",
        };

        _fileRepoMock.Setup(r => r.AddAsync(It.IsAny<DocumentFileModel>()))
            .Callback<DocumentFileModel>(m => m.Id = Guid.NewGuid())
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.CreateAsync(model);

        // Assert
        result.Status.Should().Be(DocumentFileStatus.Uploaded);
        result.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        _fileRepoMock.Verify(r => r.AddAsync(It.Is<DocumentFileModel>(m =>
            m.Status == DocumentFileStatus.Uploaded && m.FileName == "test.pdf")), Times.Once);
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
        _fileRepoMock.Setup(r => r.GetListAsync(1, 20, null))
            .ReturnsAsync((items, 2));

        // Act
        var (resultItems, totalCount) = await _service.GetListAsync(1, 20);

        // Assert
        resultItems.Should().HaveCount(2);
        totalCount.Should().Be(2);
    }

    [Fact]
    public async Task GetListAsync_FiltersByStatus()
    {
        // Arrange
        var items = new List<DocumentFileModel>
        {
            new() { Id = Guid.NewGuid(), FileName = "a.pdf", Status = DocumentFileStatus.Uploaded },
        };
        _fileRepoMock.Setup(r => r.GetListAsync(1, 20, DocumentFileStatus.Uploaded))
            .ReturnsAsync((items, 1));

        // Act
        var (resultItems, totalCount) = await _service.GetListAsync(1, 20, DocumentFileStatus.Uploaded);

        // Assert
        resultItems.Should().HaveCount(1);
        totalCount.Should().Be(1);
    }

    [Fact]
    public async Task UpdateStatusAsync_UpdatesStatusAndFields()
    {
        // Arrange
        var id = Guid.NewGuid();
        var model = new DocumentFileModel { Id = id, Status = DocumentFileStatus.Uploaded };
        _fileRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(model);
        _fileRepoMock.Setup(r => r.UpdateAsync(It.IsAny<DocumentFileModel>())).ReturnsAsync(true);

        // Act
        var result = await _service.UpdateStatusAsync(id, DocumentFileStatus.Parsing, externalTaskId: "task-123");

        // Assert
        result.Status.Should().Be(DocumentFileStatus.Parsing);
        result.ExternalTaskId.Should().Be("task-123");
        result.UpdatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        _fileRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentFileModel>(m =>
            m.Status == DocumentFileStatus.Parsing && m.ExternalTaskId == "task-123")), Times.Once);
    }

    [Fact]
    public async Task UpdateStatusAsync_SetsParsedAt_WhenStatusIsParsed()
    {
        // Arrange
        var id = Guid.NewGuid();
        var model = new DocumentFileModel { Id = id, Status = DocumentFileStatus.Parsing };
        _fileRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(model);
        _fileRepoMock.Setup(r => r.UpdateAsync(It.IsAny<DocumentFileModel>())).ReturnsAsync(true);

        // Act
        var result = await _service.UpdateStatusAsync(id, DocumentFileStatus.Parsed, markdownContent: "# Hello");

        // Assert
        result.ParsedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        result.MarkdownContent.Should().Be("# Hello");
    }

    [Fact]
    public async Task UpdateStatusAsync_ThrowsKeyNotFound_WhenNotExists()
    {
        // Arrange
        var id = Guid.NewGuid();
        _fileRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync((DocumentFileModel?)null);

        // Act
        var act = () => _service.UpdateStatusAsync(id, DocumentFileStatus.Parsing);

        // Assert
        await act.Should().ThrowAsync<KeyNotFoundException>().WithMessage($"*{id}*");
    }

    [Fact]
    public async Task UpdateStatusAsync_SetsErrorMessage_WhenProvided()
    {
        // Arrange
        var id = Guid.NewGuid();
        var model = new DocumentFileModel { Id = id, Status = DocumentFileStatus.Parsing };
        _fileRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(model);
        _fileRepoMock.Setup(r => r.UpdateAsync(It.IsAny<DocumentFileModel>())).ReturnsAsync(true);

        // Act
        var result = await _service.UpdateStatusAsync(id, DocumentFileStatus.ParseFailed, errorMessage: "Parse error");

        // Assert
        result.ErrorMessage.Should().Be("Parse error");
    }

    [Fact]
    public async Task DeleteAsync_DeletesImagesFirstThenFile()
    {
        // Arrange
        var id = Guid.NewGuid();
        _imageRepoMock.Setup(r => r.DeleteByDocumentFileIdAsync(id)).Returns(Task.CompletedTask);
        _fileRepoMock.Setup(r => r.DeleteAsync(id)).ReturnsAsync(true);

        // Act
        var result = await _service.DeleteAsync(id);

        // Assert
        result.Should().BeTrue();
        _imageRepoMock.Verify(r => r.DeleteByDocumentFileIdAsync(id), Times.Once);
        _fileRepoMock.Verify(r => r.DeleteAsync(id), Times.Once);
    }

    [Fact]
    public async Task GetPendingParseJobsAsync_ReturnsOnlyPendingParseJobs()
    {
        // Arrange
        var items = new List<DocumentFileModel>
        {
            new() { Id = Guid.NewGuid(), Status = DocumentFileStatus.PendingParse },
        };
        _fileRepoMock.Setup(r => r.GetByStatusAsync(DocumentFileStatus.PendingParse))
            .ReturnsAsync(items);

        // Act
        var result = await _service.GetPendingParseJobsAsync();

        // Assert
        result.Should().HaveCount(1);
        result[0].Status.Should().Be(DocumentFileStatus.PendingParse);
    }

    [Fact]
    public async Task AddImageAsync_CallsRepository()
    {
        // Arrange
        var image = new DocumentFileImageModel
        {
            DocumentFileId = Guid.NewGuid(),
            ImageName = "img1.jpg",
            ImagePath = "docretrieval-images/abc/img1.jpg",
        };
        _imageRepoMock.Setup(r => r.AddAsync(image)).Returns(Task.CompletedTask);

        // Act
        await _service.AddImageAsync(image);

        // Assert
        _imageRepoMock.Verify(r => r.AddAsync(image), Times.Once);
    }

    [Fact]
    public async Task GetImagesByFileIdAsync_ReturnsImages()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        var images = new List<DocumentFileImageModel>
        {
            new() { Id = Guid.NewGuid(), DocumentFileId = fileId, ImageName = "img1.jpg" },
            new() { Id = Guid.NewGuid(), DocumentFileId = fileId, ImageName = "img2.png" },
        };
        _imageRepoMock.Setup(r => r.GetByDocumentFileIdAsync(fileId)).ReturnsAsync(images);

        // Act
        var result = await _service.GetImagesByFileIdAsync(fileId);

        // Assert
        result.Should().HaveCount(2);
    }
}
