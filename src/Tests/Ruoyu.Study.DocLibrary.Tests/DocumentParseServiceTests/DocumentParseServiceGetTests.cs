using System;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests.DocumentParseServiceTests;

public class DocumentParseServiceGetTests
{
    private readonly Mock<IDocumentParseRepository> _parseRepoMock;
    private readonly Mock<IDocumentParseImageRepository> _imageRepoMock;
    private readonly Mock<ILogger<DocumentParseService>> _loggerMock;
    private readonly DocumentParseService _service;

    public DocumentParseServiceGetTests()
    {
        _parseRepoMock = new Mock<IDocumentParseRepository>();
        _imageRepoMock = new Mock<IDocumentParseImageRepository>();
        _loggerMock = new Mock<ILogger<DocumentParseService>>();
        _service = new DocumentParseService(_parseRepoMock.Object, _imageRepoMock.Object, _loggerMock.Object);
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
    public async Task GetLatestByFileIdAndModelAsync_ReturnsParseForSpecificModel()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        var model = new DocumentParseModel { Id = Guid.NewGuid(), DocumentFileId = fileId, ModelVersion = "pipeline" };
        _parseRepoMock.Setup(r => r.GetLatestByFileIdAndModelAsync(fileId, "pipeline")).ReturnsAsync(model);

        // Act
        var result = await _service.GetLatestByFileIdAndModelAsync(fileId, "pipeline");

        // Assert
        result.Should().NotBeNull();
        result!.ModelVersion.Should().Be("pipeline");
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
}
