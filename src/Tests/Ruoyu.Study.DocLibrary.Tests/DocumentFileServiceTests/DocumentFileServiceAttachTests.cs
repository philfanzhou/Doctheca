using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Services;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests.DocumentFileServiceTests;

public class DocumentFileServiceAttachTests
{
    private readonly Mock<IDocumentFileRepository> _fileRepoMock;
    private readonly Mock<ILogger<DocumentFileService>> _loggerMock;
    private readonly DocumentFileService _service;

    public DocumentFileServiceAttachTests()
    {
        _fileRepoMock = new Mock<IDocumentFileRepository>();
        _loggerMock = new Mock<ILogger<DocumentFileService>>();
        _service = new DocumentFileService(_fileRepoMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task AttachStructaDocDocumentAsync_UpdatesReferenceAndReturnsModel()
    {
        // Arrange
        var id = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var stored = new DocumentFileModel { Id = id, StructaDocDocumentId = documentId };
        _fileRepoMock.Setup(r => r.AttachStructaDocDocumentAsync(id, documentId)).ReturnsAsync(true);
        _fileRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(stored);

        // Act
        var result = await _service.AttachStructaDocDocumentAsync(id, documentId);

        // Assert
        result.Should().NotBeNull();
        result!.StructaDocDocumentId.Should().Be(documentId);
    }

    [Fact]
    public async Task AttachStructaDocDocumentAsync_ReturnsNull_WhenFileMissing()
    {
        // Arrange
        var id = Guid.NewGuid();
        _fileRepoMock.Setup(r => r.AttachStructaDocDocumentAsync(id, It.IsAny<Guid>())).ReturnsAsync(false);

        // Act
        var result = await _service.AttachStructaDocDocumentAsync(id, Guid.NewGuid());

        // Assert
        result.Should().BeNull();
        _fileRepoMock.Verify(r => r.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
    }
}
