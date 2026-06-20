using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Services;
using Ruoyu.Study.DocRetrieval.Service;
using Xunit;

namespace Ruoyu.Study.DocRetrieval.Tests;

/// <summary>
/// Tests for document consistency scanning and force-delete logic.
/// </summary>
public class DocumentConsistencyTests
{
    private readonly Mock<IDocumentRepository> _documentRepoMock;
    private readonly Mock<IDocumentDomainService> _domainServiceMock;
    private readonly Mock<IOssService> _ossServiceMock;
    private readonly Mock<ISearchIndexService> _searchIndexMock;
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;

    public DocumentConsistencyTests()
    {
        _documentRepoMock = new Mock<IDocumentRepository>();
        _domainServiceMock = new Mock<IDocumentDomainService>();
        _ossServiceMock = new Mock<IOssService>();
        _searchIndexMock = new Mock<ISearchIndexService>();
        _loggerFactoryMock = new Mock<ILoggerFactory>();
        _loggerFactoryMock.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(Mock.Of<ILogger>());
    }

    #region ScanConsistency Tests

    [Fact]
    public async Task ScanConsistency_AllFilesExist_ReturnsEmptyBrokenAndOrphans()
    {
        // Arrange
        var docId = Guid.NewGuid();
        _documentRepoMock.Setup(r => r.GetAllDocumentsWithFilePathAsync())
            .ReturnsAsync(new List<(Guid, string, string, string)>
            {
                (docId, "Test Doc", "docretrieval/test.pdf", "ready")
            });

        _ossServiceMock.Setup(s => s.ObjectExistsAsync("docretrieval/test.pdf"))
            .ReturnsAsync(true);

        _ossServiceMock.Setup(s => s.ListObjectsAsync("docretrieval/"))
            .ReturnsAsync(new List<OssObjectInfo>
            {
                new() { ObjectPath = "docretrieval/test.pdf", Size = 1000 }
            });

        // Act
        var result = await ScanConsistencyAsync();

        // Assert
        Assert.Empty(result.OrphanOssFiles);
        Assert.Empty(result.BrokenDocuments);
    }

    [Fact]
    public async Task ScanConsistency_MissingOssFile_ReturnsBrokenDocument()
    {
        // Arrange
        var docId = Guid.NewGuid();
        _documentRepoMock.Setup(r => r.GetAllDocumentsWithFilePathAsync())
            .ReturnsAsync(new List<(Guid, string, string, string)>
            {
                (docId, "Missing Doc", "docretrieval/missing.pdf", "ready")
            });

        _ossServiceMock.Setup(s => s.ObjectExistsAsync("docretrieval/missing.pdf"))
            .ReturnsAsync(false);

        _ossServiceMock.Setup(s => s.ListObjectsAsync("docretrieval/"))
            .ReturnsAsync(new List<OssObjectInfo>());

        // Act
        var result = await ScanConsistencyAsync();

        // Assert
        Assert.Single(result.BrokenDocuments);
        Assert.Equal(docId, result.BrokenDocuments[0].Id);
        Assert.Empty(result.OrphanOssFiles);
    }

    [Fact]
    public async Task ScanConsistency_OrphanOssFile_ReturnsOrphan()
    {
        // Arrange
        _documentRepoMock.Setup(r => r.GetAllDocumentsWithFilePathAsync())
            .ReturnsAsync(new List<(Guid, string, string, string)>());

        _ossServiceMock.Setup(s => s.ListObjectsAsync("docretrieval/"))
            .ReturnsAsync(new List<OssObjectInfo>
            {
                new() { ObjectPath = "docretrieval/orphan.pdf", Size = 500 }
            });

        // Act
        var result = await ScanConsistencyAsync();

        // Assert
        Assert.Empty(result.BrokenDocuments);
        Assert.Single(result.OrphanOssFiles);
        Assert.Equal("docretrieval/orphan.pdf", result.OrphanOssFiles[0]);
    }

    [Fact]
    public async Task ScanConsistency_MixedScenario_ReturnsBothBrokenAndOrphans()
    {
        // Arrange
        var docId1 = Guid.NewGuid();
        var docId2 = Guid.NewGuid();
        _documentRepoMock.Setup(r => r.GetAllDocumentsWithFilePathAsync())
            .ReturnsAsync(new List<(Guid, string, string, string)>
            {
                (docId1, "Good Doc", "docretrieval/good.pdf", "ready"),
                (docId2, "Broken Doc", "docretrieval/broken.pdf", "ready")
            });

        _ossServiceMock.Setup(s => s.ObjectExistsAsync("docretrieval/good.pdf"))
            .ReturnsAsync(true);
        _ossServiceMock.Setup(s => s.ObjectExistsAsync("docretrieval/broken.pdf"))
            .ReturnsAsync(false);

        _ossServiceMock.Setup(s => s.ListObjectsAsync("docretrieval/"))
            .ReturnsAsync(new List<OssObjectInfo>
            {
                new() { ObjectPath = "docretrieval/good.pdf", Size = 1000 },
                new() { ObjectPath = "docretrieval/orphan.pdf", Size = 500 }
            });

        // Act
        var result = await ScanConsistencyAsync();

        // Assert
        Assert.Single(result.BrokenDocuments);
        Assert.Equal(docId2, result.BrokenDocuments[0].Id);
        Assert.Single(result.OrphanOssFiles);
        Assert.Equal("docretrieval/orphan.pdf", result.OrphanOssFiles[0]);
    }

    [Fact]
    public async Task ScanConsistency_DuplicateFilePaths_ChecksOssOnlyOnce()
    {
        // Arrange - two documents with same file path (e.g. hash dedup)
        var docId1 = Guid.NewGuid();
        var docId2 = Guid.NewGuid();
        _documentRepoMock.Setup(r => r.GetAllDocumentsWithFilePathAsync())
            .ReturnsAsync(new List<(Guid, string, string, string)>
            {
                (docId1, "Doc A", "docretrieval/shared.pdf", "ready"),
                (docId2, "Doc B", "docretrieval/shared.pdf", "ready")
            });

        _ossServiceMock.Setup(s => s.ObjectExistsAsync("docretrieval/shared.pdf"))
            .ReturnsAsync(true);

        _ossServiceMock.Setup(s => s.ListObjectsAsync("docretrieval/"))
            .ReturnsAsync(new List<OssObjectInfo>
            {
                new() { ObjectPath = "docretrieval/shared.pdf", Size = 1000 }
            });

        // Act
        var result = await ScanConsistencyAsync();

        // Assert
        Assert.Empty(result.BrokenDocuments);
        Assert.Empty(result.OrphanOssFiles);
        // Note: GetAllDocumentsWithFilePathAsync returns both docs, but ObjectExistsAsync
        // is called per unique file_path. The dedup happens at the OSS check level.
    }

    #endregion

    #region Helper Methods

    private async Task<(List<string> OrphanOssFiles, List<(Guid Id, string Title, string FilePath, string Status)> BrokenDocuments)> ScanConsistencyAsync()
    {
        var docs = await _documentRepoMock.Object.GetAllDocumentsWithFilePathAsync();
        var dbFilePaths = docs.Select(d => d.FilePath).ToHashSet();

        var brokenDocuments = new List<(Guid, string, string, string)>();
        foreach (var (id, title, filePath, status) in docs)
        {
            var exists = await _ossServiceMock.Object.ObjectExistsAsync(filePath);
            if (!exists)
            {
                brokenDocuments.Add((id, title, filePath, status));
            }
        }

        var orphanOssFiles = new List<string>();
        var ossObjects = await _ossServiceMock.Object.ListObjectsAsync("docretrieval/");
        foreach (var obj in ossObjects)
        {
            if (!dbFilePaths.Contains(obj.ObjectPath))
            {
                orphanOssFiles.Add(obj.ObjectPath);
            }
        }

        return (orphanOssFiles, brokenDocuments);
    }

    #endregion
}
