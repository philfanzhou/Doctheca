using System;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Doctheca.Tests.DocumentFileServiceTests;

public class DocumentFileServiceDeleteTests
{
    private readonly Mock<IDocumentFileRepository> _fileRepoMock;
    private readonly Mock<ILogger<DocumentFileService>> _loggerMock;
    private readonly DocumentFileService _service;

    public DocumentFileServiceDeleteTests()
    {
        _fileRepoMock = new Mock<IDocumentFileRepository>();
        _loggerMock = new Mock<ILogger<DocumentFileService>>();
        _service = new DocumentFileService(_fileRepoMock.Object, _loggerMock.Object);
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
