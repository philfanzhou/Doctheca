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

public class DocumentParseServiceCreateTests
{
    private readonly Mock<IDocumentParseRepository> _parseRepoMock;
    private readonly Mock<IDocumentParseImageRepository> _imageRepoMock;
    private readonly Mock<ILogger<DocumentParseService>> _loggerMock;
    private readonly DocumentParseService _service;

    public DocumentParseServiceCreateTests()
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
    public async Task CreateAsync_SetsModelVersion_ToVlm_ByDefault()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        _parseRepoMock.Setup(r => r.AddAsync(It.IsAny<DocumentParseModel>()))
            .Callback<DocumentParseModel>(m => { m.Id = Guid.NewGuid(); })
            .ReturnsAsync((DocumentParseModel m) => m);

        // Act
        var result = await _service.CreateAsync(fileId);

        // Assert
        result.ModelVersion.Should().Be("vlm");
    }

    [Fact]
    public async Task CreateAsync_SetsModelVersion_ToPipeline_WhenSpecified()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        _parseRepoMock.Setup(r => r.AddAsync(It.IsAny<DocumentParseModel>()))
            .Callback<DocumentParseModel>(m => { m.Id = Guid.NewGuid(); })
            .ReturnsAsync((DocumentParseModel m) => m);

        // Act
        var result = await _service.CreateAsync(fileId, "pipeline");

        // Assert
        result.ModelVersion.Should().Be("pipeline");
    }
}
