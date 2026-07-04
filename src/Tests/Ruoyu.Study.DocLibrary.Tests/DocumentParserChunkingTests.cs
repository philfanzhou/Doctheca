using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.DocLibrary.Service;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

public class DocumentParserChunkingTests
{
    [Fact]
    public void ChunkByCapacity_EmptyPages_ReturnsEmpty()
    {
        var chunks = DocumentParserService.ChunkByCapacity([], 1000);
        Assert.Empty(chunks);
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
        Assert.Equal(3, chunks[0].PageRanges.Count);
    }

    [Fact]
    public void MapOffsetToPage_PageRangesMapCorrectly()
    {
        var pages = new List<(int, string)>
        {
            (1, "AAAA"),
            (2, "BBBB")
        };
        var chunks = DocumentParserService.ChunkByCapacity(pages, 1000);

        Assert.Single(chunks);
        Assert.Equal(1, DocumentParserService.MapOffsetToPage(chunks[0].PageRanges, 0));
        Assert.Equal(2, DocumentParserService.MapOffsetToPage(chunks[0].PageRanges, 6));
    }

    [Fact]
    public async Task ParseAsync_WordWithExplicitPageBreak_CreatesMultiplePages()
    {
        var docxBytes = CreateDocxWithPageBreak();
        using var stream = new MemoryStream(docxBytes);
        var service = new DocumentParserService(Mock.Of<ILogger<DocumentParserService>>());

        var result = await service.ParseAsync(stream, "word");

        Assert.Equal(2, result.Pages.Count);
        Assert.Equal(1, result.Pages[0].PageNumber);
        Assert.Equal(2, result.Pages[1].PageNumber);
    }

    [Fact]
    public async Task ParseAsync_SentenceId_HasCorrectFormat()
    {
        var pdfBytes = CreateMinimalPdf("First sentence. Second sentence.");
        using var stream = new MemoryStream(pdfBytes);
        var service = new DocumentParserService(Mock.Of<ILogger<DocumentParserService>>());

        var result = await service.ParseAsync(stream, "pdf");

        var allSegments = result.Pages.SelectMany(p => p.Segments).ToList();
        Assert.NotEmpty(allSegments);
        Assert.All(allSegments, seg => Assert.Matches(@"^p\d+-s\d+$", seg.SentenceId));
    }

    [Fact]
    public async Task ParseAsync_SmallDocument_ProducesSegments()
    {
        var pdfBytes = CreateMinimalPdf("Hello world.");
        using var stream = new MemoryStream(pdfBytes);
        var service = new DocumentParserService(Mock.Of<ILogger<DocumentParserService>>());

        var result = await service.ParseAsync(stream, "pdf");

        Assert.NotEmpty(result.Pages.SelectMany(p => p.Segments));
    }

    private static byte[] CreateDocxWithPageBreak()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = new Body();

            body.Append(CreateParagraph("Page one content."));

            var breakPara = new Paragraph();
            var breakRun = new Run();
            breakRun.Append(new Break { Type = BreakValues.Page });
            breakPara.Append(breakRun);
            body.Append(breakPara);

            body.Append(CreateParagraph("Page two content."));

            mainPart.Document.Append(body);
            mainPart.Document.Save();
        }

        return ms.ToArray();
    }

    private static Paragraph CreateParagraph(string text)
    {
        return new Paragraph(new Run(new Text(text)));
    }

    private static byte[] CreateMinimalPdf(string text)
    {
        return Encoding.ASCII.GetBytes($"""
            %PDF-1.4
            1 0 obj
            << /Type /Catalog /Pages 2 0 R >>
            endobj
            2 0 obj
            << /Type /Pages /Kids [3 0 R] /Count 1 >>
            endobj
            3 0 obj
            << /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>
            endobj
            4 0 obj
            << /Length 44 >>
            stream
            BT
            /F1 12 Tf
            72 720 Td
            ({text}) Tj
            ET
            endstream
            endobj
            5 0 obj
            << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>
            endobj
            xref
            0 6
            0000000000 65535 f
            0000000010 00000 n
            0000000060 00000 n
            0000000117 00000 n
            0000000243 00000 n
            0000000336 00000 n
            trailer
            << /Size 6 /Root 1 0 R >>
            startxref
            406
            %%EOF
            """);
    }
}
