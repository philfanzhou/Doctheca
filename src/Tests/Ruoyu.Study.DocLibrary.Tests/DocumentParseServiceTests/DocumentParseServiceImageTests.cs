using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests.DocumentParseServiceTests;

public class DocumentParseServiceImageTests
{
    private readonly Mock<IDocumentParseRepository> _parseRepoMock;
    private readonly Mock<IDocumentParseImageRepository> _imageRepoMock;
    private readonly Mock<ILogger<DocumentParseService>> _loggerMock;
    private readonly DocumentParseService _service;

    public DocumentParseServiceImageTests()
    {
        _parseRepoMock = new Mock<IDocumentParseRepository>();
        _imageRepoMock = new Mock<IDocumentParseImageRepository>();
        _loggerMock = new Mock<ILogger<DocumentParseService>>();
        _service = new DocumentParseService(_parseRepoMock.Object, _imageRepoMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task AddImageAsync_CallsRepository()
    {
        // Arrange
        var image = new DocumentParseImageModel
        {
            ParseId = Guid.NewGuid(),
            ImageName = "img1.jpg",
            ImagePath = "doclibrary-images/abc/img1.jpg",
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
}
