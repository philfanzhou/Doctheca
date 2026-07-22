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

public class DocumentParseServiceListTests
{
    private readonly Mock<IDocumentParseRepository> _parseRepoMock;
    private readonly Mock<IDocumentParseImageRepository> _imageRepoMock;
    private readonly Mock<ILogger<DocumentParseService>> _loggerMock;
    private readonly DocumentParseService _service;

    public DocumentParseServiceListTests()
    {
        _parseRepoMock = new Mock<IDocumentParseRepository>();
        _imageRepoMock = new Mock<IDocumentParseImageRepository>();
        _loggerMock = new Mock<ILogger<DocumentParseService>>();
        _service = new DocumentParseService(_parseRepoMock.Object, _imageRepoMock.Object, _loggerMock.Object);
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
}
