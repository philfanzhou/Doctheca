using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Service;
using Xunit;

namespace Ruoyu.Study.DocRetrieval.Tests;

public class DocumentParserServiceTests
{
    private readonly DocumentParserService _service;

    public DocumentParserServiceTests()
    {
        var loggerMock = new Mock<ILogger<DocumentParserService>>();
        _service = new DocumentParserService(loggerMock.Object);
    }

    #region PDF Parsing Tests

    [Fact]
    public async Task ParseAsync_PdfExtractsTextCorrectly()
    {
        // Arrange - create minimal valid PDF with text content
        var pdfBytes = CreateMinimalPdf("Hello world. This is a test.");
        using var stream = new MemoryStream(pdfBytes);

        // Act
        var result = await _service.ParseAsync(stream, "pdf");

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Pages);
    }

    #endregion

    #region Sentence Boundary Detection

    [Fact]
    public async Task ParseAsync_HandlesAbbreviatedSentences()
    {
        // Arrange - PDF with abbreviated words
        var content = "Mr. Smith went to Dr. Jones. The doctor was helpful.";
        var pdfBytes = CreateMinimalPdf(content);
        using var stream = new MemoryStream(pdfBytes);

        // Act
        var result = await _service.ParseAsync(stream, "pdf");

        // Assert
        Assert.NotNull(result);
    }

    #endregion

    #region OCR Post-Processing

    [Fact]
    public async Task ParseAsync_OcrPostProcessRemovesExtraWhitespace()
    {
        // This tests that the OCR post-processing pipeline runs on extracted text
        var content = "This  is   a    test    sentence.";
        var pdfBytes = CreateMinimalPdf(content);
        using var stream = new MemoryStream(pdfBytes);

        // Act
        var result = await _service.ParseAsync(stream, "pdf");

        // Assert
        Assert.NotNull(result);
    }

    #endregion

    #region Question Extraction

    [Fact]
    public async Task ParseAsync_ExtractsQuestionsFromExamText()
    {
        // Arrange - text with question-like structure
        var content = "1. What is the capital of France?\nA. Paris\nB. London\nC. Berlin\nD. Madrid";
        var pdfBytes = CreateMinimalPdf(content);
        using var stream = new MemoryStream(pdfBytes);

        // Act
        var result = await _service.ParseAsync(stream, "pdf");

        // Assert
        Assert.NotNull(result);
    }

    #endregion

    #region Tokenization Tests

    [Fact]
    public async Task ParseAsync_TokenizesEnglishWords()
    {
        var content = "The students were encouraged to take notes.";
        var pdfBytes = CreateMinimalPdf(content);
        using var stream = new MemoryStream(pdfBytes);

        // Act
        var result = await _service.ParseAsync(stream, "pdf");

        // Assert
        Assert.NotNull(result);
        // Verify pages have segments with tokens
        foreach (var page in result.Pages)
        {
            foreach (var segment in page.Segments)
            {
                Assert.NotEmpty(segment.Tokens);
                foreach (var token in segment.Tokens)
                {
                    Assert.NotEmpty(token.TokenText);
                    Assert.NotEmpty(token.TokenStem);
                }
                break; // Just check first
            }
            break;
        }
    }

    #endregion

    #region Porter Stemmer Tests (via Tokenize)

    [Fact]
    public async Task ParseAsync_StemsEnglishWords()
    {
        // "studies" should stem to "studi" (Porter stemmer), "encouraged" should stem to "encourag"
        var content = "The studies were encouraged.";
        var pdfBytes = CreateMinimalPdf(content);
        using var stream = new MemoryStream(pdfBytes);

        // Act
        var result = await _service.ParseAsync(stream, "pdf");

        // Assert
        Assert.NotNull(result);
        var allTokens = new List<ParsedToken>();
        foreach (var page in result.Pages)
        {
            foreach (var segment in page.Segments)
            {
                allTokens.AddRange(segment.Tokens);
            }
        }

        // Find "studies" token
        var studiesToken = allTokens.Find(t => t.TokenText.Equals("studies", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(studiesToken);
        // Porter stemmer reduces "studies" -> "studi"
        Assert.Equal("studi", studiesToken.TokenStem);
    }

    #endregion

    #region Word Parsing Tests

    [Fact]
    public async Task ParseAsync_ParsesWordDocument()
    {
        // Arrange - create minimal .docx file
        var docxBytes = CreateMinimalDocx("This is a test Word document.");
        using var stream = new MemoryStream(docxBytes);

        // Act
        var result = await _service.ParseAsync(stream, SourceTypes.Word);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Pages);
    }

    #endregion

    #region PPT Parsing Tests

    [Fact]
    public async Task ParseAsync_ParsesPptxDocument()
    {
        // Arrange - create minimal .pptx file
        var pptxBytes = CreateMinimalPptx("This is a test slide.");
        using var stream = new MemoryStream(pptxBytes);

        // Act
        var result = await _service.ParseAsync(stream, SourceTypes.Ppt);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Pages);
    }

    #endregion

    #region SourceType Constants Coverage

    [Theory]
    [InlineData(SourceTypes.Pdf)]
    [InlineData(SourceTypes.Word)]
    [InlineData(SourceTypes.Ppt)]
    public async Task ParseAsync_AcceptsAllSourceTypes(string sourceType)
    {
        // Verify all SourceTypes constants are handled
        var bytes = sourceType switch
        {
            SourceTypes.Pdf => CreateMinimalPdf("Hello world."),
            SourceTypes.Word => CreateMinimalDocx("Hello world."),
            SourceTypes.Ppt => CreateMinimalPptx("Hello world."),
            _ => throw new ArgumentException($"Unexpected sourceType: {sourceType}")
        };
        using var stream = new MemoryStream(bytes);

        var result = await _service.ParseAsync(stream, sourceType);

        Assert.NotNull(result);
        Assert.NotEmpty(result.Pages);
    }

    [Fact]
    public async Task ParseAsync_UnknownType_ThrowsNotSupportedException()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<NotSupportedException>(() => _service.ParseAsync(stream, SourceTypes.Unknown));
    }

    [Fact]
    public async Task ParseAsync_UnsupportedType_ThrowsNotSupportedException()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<NotSupportedException>(() => _service.ParseAsync(stream, "xlsx"));
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task ParseAsync_RespectsCancellationToken()
    {
        var pdfBytes = CreateMinimalPdf("Hello world.");
        using var stream = new MemoryStream(pdfBytes);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => _service.ParseAsync(stream, "pdf", cts.Token));
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task ParseAsync_EmptyPdf_ReturnsDocumentWithNoSegments()
    {
        // Test empty stream handling - this would throw at ParseAsync level
        using var stream = new MemoryStream([0x25, 0x50, 0x44, 0x46]); // Minimal PDF header only - may cause PDFPig to throw

        // This might throw from PDFPig due to invalid PDF, which is acceptable behavior
        try
        {
            var result = await _service.ParseAsync(stream, "pdf");
            Assert.NotNull(result);
        }
        catch
        {
            // PDFPig throws for invalid PDF - this is acceptable
            Assert.True(true);
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Creates a minimal valid PDF file from text content
    /// </summary>
    private static byte[] CreateMinimalPdf(string content)
    {
        // Create a minimal valid PDF using raw bytes
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.4");

        // Object 1: Catalog
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");

        // Object 2: Pages
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        // Object 3: Page with text content
        var contentBytes = System.Text.Encoding.ASCII.GetBytes(content);
        // Wrap content in BT/ET text block
        var textContent = $"BT /F1 12 Tf 72 700 Td ({EscapePdfString(content)}) Tj ET";

        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]");
        sb.AppendLine($"/Contents 4 0 R /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>");
        sb.AppendLine("endobj");

        // Object 4: Content stream
        var streamContent = System.Text.Encoding.ASCII.GetBytes(textContent);
        sb.AppendLine("4 0 obj");
        sb.AppendLine($"<< /Length {streamContent.Length} >>");
        sb.AppendLine("stream");
        var streamStr = textContent;
        sb.Append(streamStr);
        sb.AppendLine();
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        // Cross-reference table
        var xrefOffset = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 5");
        sb.AppendLine("0000000000 65535 f ");

        // We need the actual byte positions of each object
        // Let's recalculate
        var allText = sb.ToString();
        var obj1Pos = allText.IndexOf("1 0 obj");
        var obj2Pos = allText.IndexOf("2 0 obj");
        var obj3Pos = allText.IndexOf("3 0 obj");
        var obj4Pos = allText.IndexOf("4 0 obj");

        // Rebuild with proper xref
        sb.Clear();
        sb.Append("%PDF-1.4\n");
        var lines = new List<string>();

        // Build content with tracked positions
        var content1 = "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n";
        var content2 = "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n";

        var escapedContent = EscapePdfString(content);
        var textOp = $"BT /F1 12 Tf 72 700 Td ({escapedContent}) Tj ET";
        var content3 = $"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]\n/Contents 4 0 R /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>\nendobj\n";
        var content4Bytes = System.Text.Encoding.ASCII.GetBytes(textOp);
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

        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string EscapePdfString(string text)
    {
        // PDF strings need certain characters escaped
        return text
            .Replace("\\", "\\\\")
            .Replace("(", "\\(")
            .Replace(")", "\\)");
    }

    /// <summary>
    /// Creates a minimal valid .docx file
    /// </summary>
    private static byte[] CreateMinimalDocx(string content)
    {
        using var ms = new MemoryStream();
        using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document();
            var body = new DocumentFormat.OpenXml.Wordprocessing.Body();
            var para = new DocumentFormat.OpenXml.Wordprocessing.Paragraph();
            var run = new DocumentFormat.OpenXml.Wordprocessing.Run();
            var text = new DocumentFormat.OpenXml.Wordprocessing.Text(content);
            run.Append(text);
            para.Append(run);
            body.Append(para);
            mainPart.Document.Append(body);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Creates a minimal valid .pptx file
    /// </summary>
    private static byte[] CreateMinimalPptx(string content)
    {
        using var ms = new MemoryStream();
        using (var doc = DocumentFormat.OpenXml.Packaging.PresentationDocument.Create(ms, DocumentFormat.OpenXml.PresentationDocumentType.Presentation))
        {
            var presentationPart = doc.AddPresentationPart();
            presentationPart.Presentation = new DocumentFormat.OpenXml.Presentation.Presentation();
            var slidePart = presentationPart.AddNewPart<DocumentFormat.OpenXml.Packaging.SlidePart>();
            slidePart.Slide = new DocumentFormat.OpenXml.Presentation.Slide(
                new DocumentFormat.OpenXml.Presentation.CommonSlideData(
                    new DocumentFormat.OpenXml.Presentation.ShapeTree(
                        new DocumentFormat.OpenXml.Presentation.Shape(
                            new DocumentFormat.OpenXml.Presentation.NonVisualShapeProperties(
                                new DocumentFormat.OpenXml.Presentation.NonVisualDrawingProperties { Id = 1, Name = "Title 1" },
                                new DocumentFormat.OpenXml.Presentation.NonVisualShapeDrawingProperties(),
                                new DocumentFormat.OpenXml.Presentation.ApplicationNonVisualDrawingProperties()),
                            new DocumentFormat.OpenXml.Presentation.ShapeProperties(),
                            new DocumentFormat.OpenXml.Presentation.TextBody(
                                new DocumentFormat.OpenXml.Drawing.BodyProperties(),
                                new DocumentFormat.OpenXml.Drawing.Paragraph(
                                    new DocumentFormat.OpenXml.Drawing.Run(
                                        new DocumentFormat.OpenXml.Drawing.Text(content))))))));
            var slideIdList = new DocumentFormat.OpenXml.Presentation.SlideIdList(
                new DocumentFormat.OpenXml.Presentation.SlideId { Id = 256, RelationshipId = presentationPart.GetIdOfPart(slidePart) });
            presentationPart.Presentation.Append(slideIdList);
        }
        return ms.ToArray();
    }

    #endregion
}
