using System;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Doctheca.Tests.DocumentParseServiceTests;

public class DocumentParseServiceDeleteTests
{
    private readonly Mock<IDocumentParseRepository> _parseRepoMock;
    private readonly Mock<IDocumentParseImageRepository> _imageRepoMock;
    private readonly Mock<ILogger<DocumentParseService>> _loggerMock;
    private readonly DocumentParseService _service;

    public DocumentParseServiceDeleteTests()
    {
        _parseRepoMock = new Mock<IDocumentParseRepository>();
        _imageRepoMock = new Mock<IDocumentParseImageRepository>();
        _loggerMock = new Mock<ILogger<DocumentParseService>>();
        _service = new DocumentParseService(_parseRepoMock.Object, _imageRepoMock.Object, _loggerMock.Object);
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
