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

public class DocumentFileServiceCreateTests
{
    private readonly Mock<IDocumentFileRepository> _fileRepoMock;
    private readonly Mock<ILogger<DocumentFileService>> _loggerMock;
    private readonly DocumentFileService _service;

    public DocumentFileServiceCreateTests()
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
            FilePath = "doctheca-files/abc.pdf",
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
}
