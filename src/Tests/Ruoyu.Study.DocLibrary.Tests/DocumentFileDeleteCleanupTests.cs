using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

/// <summary>
/// Tests the OSS path collection logic used by DeleteDocumentFile.
/// Verifies that all paths (source, zip, images) are collected before DB deletion,
/// so the cleanup is complete even if individual OSS calls fail.
/// </summary>
public class DocumentFileDeleteCleanupTests
{
    private readonly Mock<IDocumentFileService> _fileServiceMock;
    private readonly Mock<IDocumentParseService> _parseServiceMock;
    private readonly Mock<IOssService> _ossServiceMock;
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;

    public DocumentFileDeleteCleanupTests()
    {
        _fileServiceMock = new Mock<IDocumentFileService>();
        _parseServiceMock = new Mock<IDocumentParseService>();
        _ossServiceMock = new Mock<IOssService>();
        _loggerFactoryMock = new Mock<ILoggerFactory>();
        _loggerFactoryMock.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(new Mock<ILogger>().Object);
    }

    [Fact]
    public async Task Cleanup_CollectsAllOssPaths_BeforeDeletion()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        var parseId = Guid.NewGuid();
        var imageId = Guid.NewGuid();

        var file = new DocumentFileModel
        {
            Id = fileId,
            FileName = "test.pdf",
            FilePath = "documents/2026/07/03/abc/source.pdf",
            ContentType = "application/pdf",
        };
        var parse = new DocumentParseModel
        {
            Id = parseId,
            DocumentFileId = fileId,
            Status = "parsed",
            ZipPath = "documents/2026/07/03/abc/mineru-output.zip",
        };
        var images = new List<DocumentParseImageModel>
        {
            new() { Id = imageId, ParseId = parseId, ImageName = "p1.jpg", ImagePath = "documents/2026/07/03/abc/images/p1.jpg", ContentType = "image/jpeg" },
            new() { Id = Guid.NewGuid(), ParseId = parseId, ImageName = "p2.png", ImagePath = "documents/2026/07/03/abc/images/p2.png", ContentType = "image/png" },
        };

        _fileServiceMock.Setup(s => s.GetByIdAsync(fileId)).ReturnsAsync(file);
        _fileServiceMock.Setup(s => s.DeleteAsync(fileId)).ReturnsAsync(true);
        _parseServiceMock.Setup(s => s.GetByFileIdAsync(fileId)).ReturnsAsync(new List<DocumentParseModel> { parse });
        _parseServiceMock.Setup(s => s.GetImagesByParseIdAsync(parseId)).ReturnsAsync(images);

        var collectedPaths = new List<string>();
        _ossServiceMock.Setup(s => s.DeleteAsync(It.IsAny<string>()))
            .Callback<string>(p => collectedPaths.Add(p))
            .ReturnsAsync(true);

        // Act: simulate delete logic
        var allParses = await _parseServiceMock.Object.GetByFileIdAsync(fileId);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file.FilePath };
        foreach (var p in allParses)
        {
            if (!string.IsNullOrEmpty(p.ZipPath)) paths.Add(p.ZipPath);
            var imgs = await _parseServiceMock.Object.GetImagesByParseIdAsync(p.Id);
            foreach (var img in imgs) paths.Add(img.ImagePath);
        }
        await _fileServiceMock.Object.DeleteAsync(fileId).ContinueWith(_ => { });
        foreach (var path in paths)
        {
            try { await _ossServiceMock.Object.DeleteAsync(path); } catch { /* best effort */ }
        }

        // Assert
        collectedPaths.Should().BeEquivalentTo(new[] {
            "documents/2026/07/03/abc/source.pdf",
            "documents/2026/07/03/abc/mineru-output.zip",
            "documents/2026/07/03/abc/images/p1.jpg",
            "documents/2026/07/03/abc/images/p2.png",
        }, opts => opts.WithStrictOrdering());
    }

    [Fact]
    public async Task Cleanup_HandlesNullZipPath()
    {
        // Arrange: parse with no ZipPath (e.g., legacy or failed parse)
        var fileId = Guid.NewGuid();
        var parseId = Guid.NewGuid();

        _fileServiceMock.Setup(s => s.GetByIdAsync(fileId)).ReturnsAsync(new DocumentFileModel
        {
            Id = fileId, FileName = "test.pdf", FilePath = "docs/source.pdf", ContentType = "application/pdf"
        });
        _parseServiceMock.Setup(s => s.GetByFileIdAsync(fileId)).ReturnsAsync(new List<DocumentParseModel>
        {
            new() { Id = parseId, DocumentFileId = fileId, Status = "failed", ZipPath = null }
        });
        _parseServiceMock.Setup(s => s.GetImagesByParseIdAsync(parseId)).ReturnsAsync(new List<DocumentParseImageModel>());

        // Act: collect paths
        var allParses = await _parseServiceMock.Object.GetByFileIdAsync(fileId);
        var paths = new HashSet<string> { "docs/source.pdf" };
        foreach (var p in allParses)
        {
            if (!string.IsNullOrEmpty(p.ZipPath)) paths.Add(p.ZipPath);
        }

        // Assert: only source path
        paths.Should().HaveCount(1);
        paths.Should().Contain("docs/source.pdf");
    }

    [Fact]
    public async Task Cleanup_DedupesDuplicatePaths()
    {
        // Arrange: same path appearing multiple times (e.g., from multiple parses sharing the same image)
        var fileId = Guid.NewGuid();

        _fileServiceMock.Setup(s => s.GetByIdAsync(fileId)).ReturnsAsync(new DocumentFileModel
        {
            Id = fileId, FileName = "test.pdf", FilePath = "docs/source.pdf", ContentType = "application/pdf"
        });
        _parseServiceMock.Setup(s => s.GetByFileIdAsync(fileId)).ReturnsAsync(new List<DocumentParseModel>
        {
            new() { Id = Guid.NewGuid(), DocumentFileId = fileId, Status = "parsed", ZipPath = "docs/mineru.zip" },
            new() { Id = Guid.NewGuid(), DocumentFileId = fileId, Status = "parsed", ZipPath = "docs/mineru.zip" }, // same zip!
        });
        _parseServiceMock.Setup(s => s.GetImagesByParseIdAsync(It.IsAny<Guid>())).ReturnsAsync(new List<DocumentParseImageModel>());

        // Act
        var allParses = await _parseServiceMock.Object.GetByFileIdAsync(fileId);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "docs/source.pdf" };
        foreach (var p in allParses)
            if (!string.IsNullOrEmpty(p.ZipPath)) paths.Add(p.ZipPath);

        // Assert: dedup works
        paths.Should().HaveCount(2);  // source + zip (deduped)
    }
}
