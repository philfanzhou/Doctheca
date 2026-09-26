using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Doctheca.Tests.DocumentFileServiceTests;

public class DocumentFileServiceListTests
{
    private readonly Mock<IDocumentFileRepository> _fileRepoMock;
    private readonly Mock<ILogger<DocumentFileService>> _loggerMock;
    private readonly DocumentFileService _service;

    public DocumentFileServiceListTests()
    {
        _fileRepoMock = new Mock<IDocumentFileRepository>();
        _loggerMock = new Mock<ILogger<DocumentFileService>>();
        _service = new DocumentFileService(_fileRepoMock.Object, _loggerMock.Object);
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
}
