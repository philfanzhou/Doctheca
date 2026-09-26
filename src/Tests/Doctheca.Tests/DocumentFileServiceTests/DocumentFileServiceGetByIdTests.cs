using System;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Doctheca.Tests.DocumentFileServiceTests;

public class DocumentFileServiceGetByIdTests
{
    private readonly Mock<IDocumentFileRepository> _fileRepoMock;
    private readonly Mock<ILogger<DocumentFileService>> _loggerMock;
    private readonly DocumentFileService _service;

    public DocumentFileServiceGetByIdTests()
    {
        _fileRepoMock = new Mock<IDocumentFileRepository>();
        _loggerMock = new Mock<ILogger<DocumentFileService>>();
        _service = new DocumentFileService(_fileRepoMock.Object, _loggerMock.Object);
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
}
