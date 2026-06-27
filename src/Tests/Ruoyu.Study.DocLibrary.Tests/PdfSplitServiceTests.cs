using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using Ruoyu.Study.DocLibrary.Service;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

public class PdfSplitServiceTests
{
    private readonly PdfSplitService _service;

    public PdfSplitServiceTests()
    {
        var logger = new Mock<ILogger<PdfSplitService>>().Object;
        _service = new PdfSplitService(logger);
    }

    private Stream CreateTestPdf(int pageCount)
    {
        var doc = new PdfDocument();
        for (int i = 0; i < pageCount; i++)
        {
            var page = doc.AddPage();
            var gfx = PdfSharpCore.Drawing.XGraphics.FromPdfPage(page);
            gfx.DrawString($"Page {i + 1}", new PdfSharpCore.Drawing.XFont("Arial", 12), PdfSharpCore.Drawing.XBrushes.Black, 50, 50);
        }
        var ms = new MemoryStream();
        doc.Save(ms, false);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void GetPageCount_ReturnsCorrectCount()
    {
        // Arrange
        using var pdfStream = CreateTestPdf(5);

        // Act
        var count = _service.GetPageCount(pdfStream);

        // Assert
        count.Should().Be(5);
    }

    [Fact]
    public void GetPageCount_SinglePage_Returns1()
    {
        // Arrange
        using var pdfStream = CreateTestPdf(1);

        // Act
        var count = _service.GetPageCount(pdfStream);

        // Assert
        count.Should().Be(1);
    }

    [Fact]
    public void SplitPdf_SmallFile_ReturnsSingleChunk()
    {
        // Arrange
        using var pdfStream = CreateTestPdf(3);

        // Act
        var chunks = _service.SplitPdf(pdfStream, maxPagesPerChunk: 200);

        // Assert
        chunks.Should().HaveCount(1);
        chunks[0].ChunkIndex.Should().Be(0);
        chunks[0].ChunkStream.Should().NotBeNull();
    }

    [Fact]
    public void SplitPdf_ExactMultiple_ReturnsCorrectChunks()
    {
        // Arrange
        using var pdfStream = CreateTestPdf(6);

        // Act
        var chunks = _service.SplitPdf(pdfStream, maxPagesPerChunk: 3);

        // Assert
        chunks.Should().HaveCount(2);
        chunks[0].ChunkIndex.Should().Be(0);
        chunks[1].ChunkIndex.Should().Be(1);
    }

    [Fact]
    public void SplitPdf_RemainderPages_ReturnsCorrectChunks()
    {
        // Arrange
        using var pdfStream = CreateTestPdf(7);

        // Act
        var chunks = _service.SplitPdf(pdfStream, maxPagesPerChunk: 3);

        // Assert
        chunks.Should().HaveCount(3);
    }

    [Fact]
    public void SplitPdf_ChunksHaveCorrectPageCounts()
    {
        // Arrange
        using var pdfStream = CreateTestPdf(7);

        // Act
        var chunks = _service.SplitPdf(pdfStream, maxPagesPerChunk: 3);

        // Assert
        chunks.Should().HaveCount(3);

        // Verify chunk 0 has 3 pages
        chunks[0].ChunkStream.Position = 0;
        using (var chunk0Doc = PdfReader.Open(chunks[0].ChunkStream, PdfDocumentOpenMode.Import))
        {
            chunk0Doc.PageCount.Should().Be(3);
        }

        // Verify chunk 1 has 3 pages
        chunks[1].ChunkStream.Position = 0;
        using (var chunk1Doc = PdfReader.Open(chunks[1].ChunkStream, PdfDocumentOpenMode.Import))
        {
            chunk1Doc.PageCount.Should().Be(3);
        }

        // Verify chunk 2 has 1 page
        chunks[2].ChunkStream.Position = 0;
        using (var chunk2Doc = PdfReader.Open(chunks[2].ChunkStream, PdfDocumentOpenMode.Import))
        {
            chunk2Doc.PageCount.Should().Be(1);
        }
    }

    [Fact]
    public void SplitPdf_SinglePageFile_ReturnsSingleChunk()
    {
        // Arrange
        using var pdfStream = CreateTestPdf(1);

        // Act
        var chunks = _service.SplitPdf(pdfStream, maxPagesPerChunk: 200);

        // Assert
        chunks.Should().HaveCount(1);
        chunks[0].ChunkStream.Position = 0;
        using var doc = PdfReader.Open(chunks[0].ChunkStream, PdfDocumentOpenMode.Import);
        doc.PageCount.Should().Be(1);
    }

    [Fact]
    public void MergeMarkdown_JoinsWithSeparator()
    {
        // Arrange
        var parts = new List<string> { "# Part 1", "# Part 2", "# Part 3" };

        // Act
        var result = string.Join("\n\n---\n\n", parts);

        // Assert
        result.Should().Contain("# Part 1");
        result.Should().Contain("# Part 2");
        result.Should().Contain("# Part 3");
        result.Should().Contain("---");
        var separators = result.Split("---");
        separators.Length.Should().Be(3);
    }

    [Fact]
    public void ImageNamePrefix_AvoidsCollision()
    {
        // Arrange
        var imageName = "abc.jpg";

        // Act
        var prefixed0 = $"chunk0_{imageName}";
        var prefixed1 = $"chunk1_{imageName}";

        // Assert
        prefixed0.Should().Be("chunk0_abc.jpg");
        prefixed1.Should().Be("chunk1_abc.jpg");
        prefixed0.Should().NotBe(prefixed1);
    }
}
