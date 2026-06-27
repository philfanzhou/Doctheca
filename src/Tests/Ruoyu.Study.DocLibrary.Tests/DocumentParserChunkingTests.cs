using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Service;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

/// <summary>
/// Tests for capacity-based chunking and page mapping logic.
/// </summary>
public class DocumentParserChunkingTests
{
    #region ChunkByCapacity Tests

    [Fact]
    public void ChunkByCapacity_EmptyPages_ReturnsEmpty()
    {
        var pages = new List<(int, string)>();
        var chunks = DocumentParserService.ChunkByCapacity(pages, 1000);
        Assert.Empty(chunks);
    }

    [Fact]
    public void ChunkByCapacity_AllEmptyPages_ReturnsEmpty()
    {
        var pages = new List<(int, string)>
        {
            (1, ""),
            (2, "   "),
            (3, "")
        };
        var chunks = DocumentParserService.ChunkByCapacity(pages, 1000);
        Assert.Empty(chunks);
    }

    [Fact]
    public void ChunkByCapacity_SinglePage_SmallerThanChunkSize_ReturnsOneChunk()
    {
        var pages = new List<(int, string)>
        {
            (1, "Hello world. This is a test.")
        };
        var chunks = DocumentParserService.ChunkByCapacity(pages, 1000);

        Assert.Single(chunks);
        Assert.Equal("Hello world. This is a test.", chunks[0].Text);
        Assert.Equal(0, chunks[0].GlobalStartOffset);
        Assert.Single(chunks[0].PageRanges);
        Assert.Equal(1, chunks[0].PageRanges[0].PageNumber);
    }

    [Fact]
    public void ChunkByCapacity_MultiplePages_SmallerThanChunkSize_ReturnsOneChunk()
    {
        var pages = new List<(int, string)>
        {
            (1, "Page one text."),
            (2, "Page two text."),
            (3, "Page three text.")
        };
        var chunks = DocumentParserService.ChunkByCapacity(pages, 1000);

        Assert.Single(chunks);
        Assert.Contains("Page one text.", chunks[0].Text);
        Assert.Contains("Page two text.", chunks[0].Text);
        Assert.Contains("Page three text.", chunks[0].Text);
        Assert.Equal(3, chunks[0].PageRanges.Count);
    }

    [Fact]
    public void ChunkByCapacity_TextExceedsChunkSize_SplitsIntoMultipleChunks()
    {
        var longText = new string('A', 600);
        var pages = new List<(int, string)>
        {
            (1, longText),
            (2, longText),
            (3, longText)
        };
        var chunks = DocumentParserService.ChunkByCapacity(pages, 1000);

        Assert.True(chunks.Count >= 2);
        var allText = string.Join("", chunks.Select(c => c.Text));
        Assert.Contains("A", allText);
    }

    [Fact]
    public void ChunkByCapacity_PrefersSplitAtParagraphBoundary()
    {
        var pages = new List<(int, string)>
        {
            (1, "First paragraph.\n\nSecond paragraph.\n\nThird paragraph.")
        };
        var chunks = DocumentParserService.ChunkByCapacity(pages, 30);

        Assert.True(chunks.Count >= 2);
        foreach (var chunk in chunks)
        {
            Assert.False(string.IsNullOrWhiteSpace(chunk.Text));
        }
    }

    [Fact]
    public void ChunkByCapacity_PageRangesMapCorrectly()
    {
        var pages = new List<(int, string)>
        {
            (1, "AAAA"),
            (2, "BBBB")
        };
        var chunks = DocumentParserService.ChunkByCapacity(pages, 1000);

        Assert.Single(chunks);
        var chunk = chunks[0];
        Assert.Equal(2, chunk.PageRanges.Count);

        var page1Range = chunk.PageRanges.First(r => r.PageNumber == 1);
        Assert.Equal(0, page1Range.ChunkStartOffset);
        Assert.Equal(4, page1Range.ChunkEndOffset);

        // globalStart is captured before separator is appended, so page 2 starts at 4 (includes separator)
        var page2Range = chunk.PageRanges.First(r => r.PageNumber == 2);
        Assert.Equal(4, page2Range.ChunkStartOffset);
        Assert.Equal(10, page2Range.ChunkEndOffset);
    }

    [Fact]
    public void ChunkByCapacity_GlobalStartOffset_IsCorrect()
    {
        var pages = new List<(int, string)>
        {
            (1, "AAAA"),
            (2, "BBBB")
        };
        var chunks = DocumentParserService.ChunkByCapacity(pages, 5);

        Assert.True(chunks.Count >= 2);
        Assert.Equal(0, chunks[0].GlobalStartOffset);
        Assert.True(chunks[1].GlobalStartOffset > 0);
    }

    #endregion

    #region MapOffsetToPage Tests

    [Fact]
    public void MapOffsetToPage_SinglePage_ReturnsThatPage()
    {
        var ranges = new List<DocumentParserService.PageRange>
        {
            new() { ChunkStartOffset = 0, ChunkEndOffset = 100, PageNumber = 1 }
        };
        Assert.Equal(1, DocumentParserService.MapOffsetToPage(ranges, 50));
    }

    [Fact]
    public void MapOffsetToPage_MultiplePages_ReturnsCorrectPage()
    {
        var ranges = new List<DocumentParserService.PageRange>
        {
            new() { ChunkStartOffset = 0, ChunkEndOffset = 10, PageNumber = 1 },
            new() { ChunkStartOffset = 12, ChunkEndOffset = 22, PageNumber = 2 },
            new() { ChunkStartOffset = 24, ChunkEndOffset = 34, PageNumber = 3 }
        };
        Assert.Equal(1, DocumentParserService.MapOffsetToPage(ranges, 5));
        Assert.Equal(2, DocumentParserService.MapOffsetToPage(ranges, 15));
        Assert.Equal(3, DocumentParserService.MapOffsetToPage(ranges, 25));
    }

    [Fact]
    public void MapOffsetToPage_AtBoundary_ReturnsNextPage()
    {
        var ranges = new List<DocumentParserService.PageRange>
        {
            new() { ChunkStartOffset = 0, ChunkEndOffset = 10, PageNumber = 1 },
            new() { ChunkStartOffset = 10, ChunkEndOffset = 20, PageNumber = 2 }
        };
        Assert.Equal(2, DocumentParserService.MapOffsetToPage(ranges, 10));
    }

    [Fact]
    public void MapOffsetToPage_OutOfRange_ReturnsLastPage()
    {
        var ranges = new List<DocumentParserService.PageRange>
        {
            new() { ChunkStartOffset = 0, ChunkEndOffset = 10, PageNumber = 1 }
        };
        Assert.Equal(1, DocumentParserService.MapOffsetToPage(ranges, 999));
    }

    [Fact]
    public void MapOffsetToPage_EmptyRanges_ReturnsOne()
    {
        var ranges = new List<DocumentParserService.PageRange>();
        Assert.Equal(1, DocumentParserService.MapOffsetToPage(ranges, 0));
    }

    #endregion

    #region Integration: ChunkByCapacity + MapOffsetToPage

    [Fact]
    public void ChunkAndMap_RoundTrip_CorrectlyMapsSegmentsToPages()
    {
        var pages = new List<(int, string)>
        {
            (1, "First page content here."),
            (2, "Second page content here."),
            (3, "Third page content here.")
        };
        var chunks = DocumentParserService.ChunkByCapacity(pages, 1000);

        Assert.Single(chunks);
        var chunk = chunks[0];

        var seg1Page = DocumentParserService.MapOffsetToPage(chunk.PageRanges, 0);
        var seg2Page = DocumentParserService.MapOffsetToPage(chunk.PageRanges, 10);

        Assert.Equal(1, seg1Page);
        Assert.Equal(1, seg2Page);
    }

    #endregion

    #region Word Page Break Detection via ParseAsync

    [Fact]
    public async Task ParseAsync_WordWithExplicitPageBreak_CreatesMultiplePages()
    {
        // Arrange: Word doc with explicit page break (w:br type="page")
        var llmMock = new Mock<ILlmSegmentationService>();
        llmMock.Setup(l => l.ChunkSize).Returns(1500);
        llmMock.Setup(l => l.MaxConcurrency).Returns(1);
        llmMock.Setup(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentProfile { Subject = "English", SegmentStrategy = SegmentTypes.Sentence });
        llmMock.Setup(l => l.SegmentTextAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SegmentResult>
            {
                new() { Text = "content", StartOffset = 0, EndOffset = 7, SegmentType = SegmentTypes.Sentence }
            });

        var docxBytes = CreateDocxWithPageBreak();
        using var stream = new MemoryStream(docxBytes);

        var service = new DocumentParserService(
            Mock.Of<ILogger<DocumentParserService>>(), llmMock.Object);

        // Act
        var result = await service.ParseAsync(stream, "word");

        // Assert: should have 2 pages
        Assert.Equal(2, result.Pages.Count);
        Assert.Equal(1, result.Pages[0].PageNumber);
        Assert.Equal(2, result.Pages[1].PageNumber);
    }

    [Fact]
    public async Task ParseAsync_WordWithPageBreakBefore_CreatesMultiplePages()
    {
        // Arrange: Word doc with PageBreakBefore property
        var llmMock = new Mock<ILlmSegmentationService>();
        llmMock.Setup(l => l.ChunkSize).Returns(1500);
        llmMock.Setup(l => l.MaxConcurrency).Returns(1);
        llmMock.Setup(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentProfile { Subject = "English", SegmentStrategy = SegmentTypes.Sentence });
        llmMock.Setup(l => l.SegmentTextAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SegmentResult>
            {
                new() { Text = "content", StartOffset = 0, EndOffset = 7, SegmentType = SegmentTypes.Sentence }
            });

        var docxBytes = CreateDocxWithPageBreakBefore();
        using var stream = new MemoryStream(docxBytes);

        var service = new DocumentParserService(
            Mock.Of<ILogger<DocumentParserService>>(), llmMock.Object);

        // Act
        var result = await service.ParseAsync(stream, "word");

        // Assert: should have 2 pages
        Assert.Equal(2, result.Pages.Count);
    }

    [Fact]
    public async Task ParseAsync_WordWithSectionBreak_CreatesMultiplePages()
    {
        // Arrange: Word doc with NextPage section break
        var llmMock = new Mock<ILlmSegmentationService>();
        llmMock.Setup(l => l.ChunkSize).Returns(1500);
        llmMock.Setup(l => l.MaxConcurrency).Returns(1);
        llmMock.Setup(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentProfile { Subject = "English", SegmentStrategy = SegmentTypes.Sentence });
        llmMock.Setup(l => l.SegmentTextAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SegmentResult>
            {
                new() { Text = "content", StartOffset = 0, EndOffset = 7, SegmentType = SegmentTypes.Sentence }
            });

        var docxBytes = CreateDocxWithSectionBreak();
        using var stream = new MemoryStream(docxBytes);

        var service = new DocumentParserService(
            Mock.Of<ILogger<DocumentParserService>>(), llmMock.Object);

        // Act
        var result = await service.ParseAsync(stream, "word");

        // Assert: should have 2 pages
        Assert.Equal(2, result.Pages.Count);
    }

    [Fact]
    public async Task ParseAsync_WordWithoutPageBreak_SinglePage()
    {
        // Arrange: Word doc without any page breaks
        var llmMock = new Mock<ILlmSegmentationService>();
        llmMock.Setup(l => l.ChunkSize).Returns(1500);
        llmMock.Setup(l => l.MaxConcurrency).Returns(1);
        llmMock.Setup(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentProfile { Subject = "English", SegmentStrategy = SegmentTypes.Sentence });
        llmMock.Setup(l => l.SegmentTextAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SegmentResult>
            {
                new() { Text = "content", StartOffset = 0, EndOffset = 7, SegmentType = SegmentTypes.Sentence }
            });

        var docxBytes = CreateDocxWithoutPageBreak();
        using var stream = new MemoryStream(docxBytes);

        var service = new DocumentParserService(
            Mock.Of<ILogger<DocumentParserService>>(), llmMock.Object);

        // Act
        var result = await service.ParseAsync(stream, "word");

        // Assert: should have 1 page
        Assert.Single(result.Pages);
        Assert.Equal(1, result.Pages[0].PageNumber);
    }

    #endregion

    #region SentenceId Format

    [Fact]
    public async Task ParseAsync_SentenceId_HasCorrectFormat()
    {
        var profile = new DocumentProfile
        {
            Subject = "English",
            DocType = "教材",
            SegmentStrategy = SegmentTypes.Sentence
        };

        var segments = new List<SegmentResult>
        {
            new() { Text = "First sentence.", StartOffset = 0, EndOffset = 15, SegmentType = SegmentTypes.Sentence },
            new() { Text = "Second sentence.", StartOffset = 16, EndOffset = 32, SegmentType = SegmentTypes.Sentence }
        };

        var llmMock = new Mock<ILlmSegmentationService>();
        llmMock.Setup(l => l.ChunkSize).Returns(1500);
        llmMock.Setup(l => l.MaxConcurrency).Returns(1);
        llmMock.Setup(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        llmMock.Setup(l => l.SegmentTextAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(segments);

        var service = new DocumentParserService(
            Mock.Of<ILogger<DocumentParserService>>(), llmMock.Object);
        var pdfBytes = CreateMinimalPdf("First sentence. Second sentence.");
        using var stream = new MemoryStream(pdfBytes);

        // Act
        var result = await service.ParseAsync(stream, "pdf");

        // Assert - SentenceId should be p{N}-s{K} format (no block)
        var allSegments = result.Pages.SelectMany(p => p.Segments).ToList();
        Assert.NotEmpty(allSegments);
        foreach (var seg in allSegments)
        {
            Assert.Matches(@"^p\d+-s\d+$", seg.SentenceId);
        }
    }

    #endregion

    #region LLM Call Count

    [Fact]
    public async Task ParseAsync_SmallDocument_CallsLlmOnce()
    {
        var profile = new DocumentProfile
        {
            Subject = "English",
            SegmentStrategy = SegmentTypes.Sentence
        };

        var llmMock = new Mock<ILlmSegmentationService>();
        llmMock.Setup(l => l.ChunkSize).Returns(1500);
        llmMock.Setup(l => l.MaxConcurrency).Returns(1);
        llmMock.Setup(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        llmMock.Setup(l => l.SegmentTextAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SegmentResult>
            {
                new() { Text = "Hello.", StartOffset = 0, EndOffset = 6, SegmentType = SegmentTypes.Sentence }
            });

        var service = new DocumentParserService(
            Mock.Of<ILogger<DocumentParserService>>(), llmMock.Object);
        var pdfBytes = CreateMinimalPdf("Hello world.");
        using var stream = new MemoryStream(pdfBytes);

        await service.ParseAsync(stream, "pdf");

        // Small document with ChunkSize=1500 should be 1 chunk = 1 call
        llmMock.Verify(l => l.SegmentTextAsync(
            It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Helper Methods

    private static byte[] CreateDocxWithPageBreak()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = new Body();

            // Page 1
            body.Append(CreateParagraph("Page one content."));

            // Page break via Break element
            var breakPara = new Paragraph();
            var breakRun = new Run();
            breakRun.Append(new Break { Type = BreakValues.Page });
            breakPara.Append(breakRun);
            body.Append(breakPara);

            // Page 2
            body.Append(CreateParagraph("Page two content."));

            mainPart.Document.Append(body);
            mainPart.Document.Save();
        }
        return ms.ToArray();
    }

    private static byte[] CreateDocxWithPageBreakBefore()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = new Body();

            // Page 1
            body.Append(CreateParagraph("Page one content."));

            // Page 2 - with PageBreakBefore property
            var para = CreateParagraph("Page two content.");
            para.ParagraphProperties = new ParagraphProperties(new PageBreakBefore());
            body.Append(para);

            mainPart.Document.Append(body);
            mainPart.Document.Save();
        }
        return ms.ToArray();
    }

    private static byte[] CreateDocxWithSectionBreak()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = new Body();

            // Page 1
            body.Append(CreateParagraph("Page one content."));

            // Section break (NextPage)
            var para = CreateParagraph("Page two content.");
            para.ParagraphProperties = new ParagraphProperties(
                new SectionProperties(
                    new SectionType { Val = SectionMarkValues.NextPage }));
            body.Append(para);

            mainPart.Document.Append(body);
            mainPart.Document.Save();
        }
        return ms.ToArray();
    }

    private static byte[] CreateDocxWithoutPageBreak()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = new Body();

            body.Append(CreateParagraph("First paragraph."));
            body.Append(CreateParagraph("Second paragraph."));
            body.Append(CreateParagraph("Third paragraph."));

            mainPart.Document.Append(body);
            mainPart.Document.Save();
        }
        return ms.ToArray();
    }

    private static Paragraph CreateParagraph(string text)
    {
        var para = new Paragraph();
        var run = new Run();
        run.Append(new Text(text));
        para.Append(run);
        return para;
    }

    private static byte[] CreateMinimalPdf(string content)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        var content1 = "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n";
        var content2 = "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n";

        var escapedContent = content
            .Replace("\\", "\\\\")
            .Replace("(", "\\(")
            .Replace(")", "\\)");
        var textOp = $"BT /F1 12 Tf 72 700 Td ({escapedContent}) Tj ET";
        var content3 = $"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]\n/Contents 4 0 R /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>\nendobj\n";
        var content4Bytes = Encoding.ASCII.GetBytes(textOp);
        var content4 = $"4 0 obj\n<< /Length {content4Bytes.Length} >>\nstream\n{textOp}\nendstream\nendobj\n";

        var header = "%PDF-1.4\n";
        sb.Append(header);
        var pos1 = header.Length;
        sb.Append(content1);
        var pos2 = pos1 + content1.Length;
        sb.Append(content2);
        var pos3 = pos2 + content2.Length;
        sb.Append(content3);
        var pos4 = pos3 + content3.Length;
        sb.Append(content4);

        sb.Append("xref\n");
        sb.Append("0 5\n");
        sb.Append("0000000000 65535 f \n");
        sb.Append($"{pos1:0000000010} 00000 n \n");
        sb.Append($"{pos2:0000000010} 00000 n \n");
        sb.Append($"{pos3:0000000010} 00000 n \n");
        sb.Append($"{pos4:0000000010} 00000 n \n");
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.Append("startxref\n");
        sb.Append($"{sb.ToString().IndexOf("xref") + header.Length}\n");
        sb.Append("%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    #endregion
}
