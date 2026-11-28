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

namespace Doctheca.Tests.DocumentParseServiceTests;

public class DocumentParseServiceUpdateStatusTests
{
    private readonly Mock<IDocumentParseRepository> _parseRepoMock;
    private readonly Mock<IDocumentParseImageRepository> _imageRepoMock;
    private readonly Mock<ILogger<DocumentParseService>> _loggerMock;
    private readonly DocumentParseService _service;

    public DocumentParseServiceUpdateStatusTests()
    {
        _parseRepoMock = new Mock<IDocumentParseRepository>();
        _imageRepoMock = new Mock<IDocumentParseImageRepository>();
        _loggerMock = new Mock<ILogger<DocumentParseService>>();
        _service = new DocumentParseService(_parseRepoMock.Object, _imageRepoMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task UpdateStatusAsync_UpdatesStatusAndFields()
    {
        // Arrange
        var id = Guid.NewGuid();
        var model = new DocumentParse { Id = id, Status = DocumentParseStatus.Pending };
        _parseRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(model);
        _parseRepoMock.Setup(r => r.UpdateAsync(It.IsAny<DocumentParse>())).ReturnsAsync(model);

        // Act
        var result = await _service.UpdateStatusAsync(id, DocumentParseStatus.Parsing, externalTaskId: "task-123");

        // Assert
        result.Status.Should().Be(DocumentParseStatus.Parsing);
        result.ExternalTaskId.Should().Be("task-123");
        _parseRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentParse>(m =>
            m.Status == DocumentParseStatus.Parsing && m.ExternalTaskId == "task-123")), Times.Once);
    }

    [Fact]
    public async Task UpdateStatusAsync_SetsParsedAt_WhenStatusIsParsed()
    {
        // Arrange
        var id = Guid.NewGuid();
        var model = new DocumentParse { Id = id, Status = DocumentParseStatus.Parsing };
        _parseRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(model);
        _parseRepoMock.Setup(r => r.UpdateAsync(It.IsAny<DocumentParse>())).ReturnsAsync(model);

        // Act
        var result = await _service.UpdateStatusAsync(id, DocumentParseStatus.Parsed);

        // Assert
        result.ParsedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UpdateStatusAsync_ThrowsKeyNotFound_WhenNotExists()
    {
        // Arrange
        var id = Guid.NewGuid();
        _parseRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync((DocumentParse?)null);

        // Act
        var act = () => _service.UpdateStatusAsync(id, DocumentParseStatus.Parsing);

        // Assert
        await act.Should().ThrowAsync<KeyNotFoundException>().WithMessage($"*{id}*");
    }

    [Fact]
    public async Task UpdateStatusAsync_SetsErrorMessage_WhenProvided()
    {
        // Arrange
        var id = Guid.NewGuid();
        var model = new DocumentParse { Id = id, Status = DocumentParseStatus.Parsing };
        _parseRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(model);
        _parseRepoMock.Setup(r => r.UpdateAsync(It.IsAny<DocumentParse>())).ReturnsAsync(model);

        // Act
        var result = await _service.UpdateStatusAsync(id, DocumentParseStatus.Failed, errorMessage: "Parse error");

        // Assert
        result.ErrorMessage.Should().Be("Parse error");
    }

    [Fact]
    public async Task GetActiveJobsAsync_ReturnsPendingAndParsingJobs()
    {
        // Arrange
        var items = new List<DocumentParse>
        {
            new() { Id = Guid.NewGuid(), Status = DocumentParseStatus.Pending },
            new() { Id = Guid.NewGuid(), Status = DocumentParseStatus.Parsing },
        };
        _parseRepoMock.Setup(r => r.GetByStatusesAsync(
                It.Is<IReadOnlyCollection<string>>(s =>
                    s.Contains(DocumentParseStatus.Pending) && s.Contains(DocumentParseStatus.Parsing))))
            .ReturnsAsync(items);

        // Act
        var result = await _service.GetActiveJobsAsync();

        // Assert
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task UpdateStatusAsync_SetsStructaDocParseRunId()
    {
        // Arrange
        var id = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var model = new DocumentParse { Id = id, Status = DocumentParseStatus.Pending };
        _parseRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(model);
        _parseRepoMock.Setup(r => r.UpdateAsync(It.IsAny<DocumentParse>())).ReturnsAsync(model);

        // Act
        var result = await _service.UpdateStatusAsync(
            id, DocumentParseStatus.Parsing, externalTaskId: runId.ToString("D"), structaDocParseRunId: runId);

        // Assert
        result.StructaDocParseRunId.Should().Be(runId);
        result.ExternalTaskId.Should().Be(runId.ToString("D"));
    }
}
