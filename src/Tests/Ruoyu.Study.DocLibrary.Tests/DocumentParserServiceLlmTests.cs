using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Service;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

/// <summary>
/// Tests for DocumentParserService with LLM segmentation integration
/// </summary>
public class DocumentParserServiceLlmTests
{
    private readonly Mock<ILogger<DocumentParserService>> _loggerMock;
    private readonly Mock<ILlmSegmentationService> _llmMock;

    public DocumentParserServiceLlmTests()
    {
        _loggerMock = new Mock<ILogger<DocumentParserService>>();
        _llmMock = new Mock<ILlmSegmentationService>();
        _llmMock.Setup(l => l.ChunkSize).Returns(2500);
        _llmMock.Setup(l => l.MaxConcurrency).Returns(2);
    }

    #region LLM Integration Tests

    [Fact]
    public async Task ParseAsync_WithLlm_CallsAnalyzeAndSegment()
    {
        // Arrange
        var profile = new DocumentProfile
        {
            Subject = "English",
            DocType = "教材",
            SegmentStrategy = SegmentTypes.Sentence
        };

        var segments = new List<SegmentResult>
        {
            new() { Text = "Hello world.", StartOffset = 0, EndOffset = 12, SegmentType = SegmentTypes.Sentence },
            new() { Text = "This is a test.", StartOffset = 13, EndOffset = 28, SegmentType = SegmentTypes.Sentence }
        };

        _llmMock.Setup(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        _llmMock.Setup(l => l.SegmentTextAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(segments);

        var service = CreateService(_llmMock.Object);
        var pdfBytes = CreateMinimalPdf("Hello world. This is a test.");
        using var stream = new MemoryStream(pdfBytes);

        // Act
        var result = await service.ParseAsync(stream, "pdf");

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Pages);
        _llmMock.Verify(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _llmMock.Verify(l => l.SegmentTextAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ParseAsync_WithLlm_SetsCorrectSegmentType()
    {
        // Arrange
        var profile = new DocumentProfile
        {
            Subject = "English",
            DocType = "单词表",
            SegmentStrategy = SegmentTypes.WordEntry
        };

        var segments = new List<SegmentResult>
        {
            new() { Text = "abandon v. 放弃", StartOffset = 0, EndOffset = 15, SegmentType = SegmentTypes.WordEntry },
            new() { Text = "ability n. 能力", StartOffset = 16, EndOffset = 31, SegmentType = SegmentTypes.WordEntry }
        };

        _llmMock.Setup(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        _llmMock.Setup(l => l.SegmentTextAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(segments);

        var service = CreateService(_llmMock.Object);
        var pdfBytes = CreateMinimalPdf("abandon v. 放弃\nability n. 能力");
        using var stream = new MemoryStream(pdfBytes);

        // Act
        var result = await service.ParseAsync(stream, "pdf");

        // Assert
        Assert.NotNull(result);
        foreach (var page in result.Pages)
        {
            foreach (var segment in page.Segments)
            {
                Assert.Equal(SegmentTypes.WordEntry, segment.SegmentType);
            }
        }
    }

    [Fact]
    public async Task ParseAsync_LlmAnalysisFails_FallsBackToRuleBased()
    {
        // Arrange
        _llmMock.Setup(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("LLM API error"));

        var service = CreateService(_llmMock.Object);
        var pdfBytes = CreateMinimalPdf("Hello world. This is a test.");
        using var stream = new MemoryStream(pdfBytes);

        // Act
        var result = await service.ParseAsync(stream, "pdf");

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Pages);
        // Should still have segments from rule-based splitting
        var totalSegments = 0;
        foreach (var page in result.Pages)
        {
            totalSegments += page.Segments.Count;
        }
        Assert.True(totalSegments > 0);
    }

    [Fact]
    public async Task ParseAsync_LlmSegmentationFails_FallsBackToRuleBased()
    {
        // Arrange
        var profile = new DocumentProfile
        {
            Subject = "English",
            DocType = "教材",
            SegmentStrategy = SegmentTypes.Sentence
        };

        _llmMock.Setup(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        _llmMock.Setup(l => l.SegmentTextAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("LLM segmentation error"));

        var service = CreateService(_llmMock.Object);
        var pdfBytes = CreateMinimalPdf("Hello world. This is a test.");
        using var stream = new MemoryStream(pdfBytes);

        // Act
        var result = await service.ParseAsync(stream, "pdf");

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Pages);
        // Should have segments from fallback
        var totalSegments = 0;
        foreach (var page in result.Pages)
        {
            totalSegments += page.Segments.Count;
        }
        Assert.True(totalSegments > 0);
    }

    [Fact]
    public async Task ParseAsync_LlmReturnsEmptySegments_FallsBackToRuleBased()
    {
        // Arrange
        var profile = new DocumentProfile
        {
            Subject = "English",
            DocType = "教材",
            SegmentStrategy = SegmentTypes.Sentence
        };

        _llmMock.Setup(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        _llmMock.Setup(l => l.SegmentTextAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SegmentResult>());

        var service = CreateService(_llmMock.Object);
        var pdfBytes = CreateMinimalPdf("Hello world. This is a test.");
        using var stream = new MemoryStream(pdfBytes);

        // Act
        var result = await service.ParseAsync(stream, "pdf");

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Pages);
    }

    [Fact]
    public async Task ParseAsync_WithoutLlm_UsesRuleBasedSplitting()
    {
        // Arrange - no LLM service
        var service = CreateService(llmService: null);
        var pdfBytes = CreateMinimalPdf("Hello world. This is a test.");
        using var stream = new MemoryStream(pdfBytes);

        // Act
        var result = await service.ParseAsync(stream, "pdf");

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Pages);
        // All segments should be sentence type (default)
        foreach (var page in result.Pages)
        {
            foreach (var segment in page.Segments)
            {
                Assert.Equal(SegmentTypes.Sentence, segment.SegmentType);
            }
        }
    }

    [Fact]
    public async Task ParseAsync_WordWithLlm_UsesLlmSegmentation()
    {
        // Arrange
        var profile = new DocumentProfile
        {
            Subject = "English",
            DocType = "教材",
            SegmentStrategy = SegmentTypes.Sentence
        };

        var segments = new List<SegmentResult>
        {
            new() { Text = "This is a test.", StartOffset = 0, EndOffset = 15, SegmentType = SegmentTypes.Sentence }
        };

        _llmMock.Setup(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        _llmMock.Setup(l => l.SegmentTextAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(segments);

        var service = CreateService(_llmMock.Object);
        var docxBytes = CreateMinimalDocx("This is a test.");
        using var stream = new MemoryStream(docxBytes);

        // Act
        var result = await service.ParseAsync(stream, "word");

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Pages);
        _llmMock.Verify(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ParseAsync_PptWithLlm_UsesLlmSegmentation()
    {
        // Arrange
        var profile = new DocumentProfile
        {
            Subject = "English",
            DocType = "教材",
            SegmentStrategy = SegmentTypes.Sentence
        };

        var segments = new List<SegmentResult>
        {
            new() { Text = "This is a slide.", StartOffset = 0, EndOffset = 16, SegmentType = SegmentTypes.Sentence }
        };

        _llmMock.Setup(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        _llmMock.Setup(l => l.SegmentTextAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(segments);

        var service = CreateService(_llmMock.Object);
        var pptxBytes = CreateMinimalPptx("This is a slide.");
        using var stream = new MemoryStream(pptxBytes);

        // Act
        var result = await service.ParseAsync(stream, "ppt");

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Pages);
        _llmMock.Verify(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ParseAsync_WordEntryLlmFails_ThrowsAndDoesNotCreateBigRecord()
    {
        // Arrange: word_entry profile, LLM throws
        var profile = new DocumentProfile
        {
            Subject = "English",
            DocType = "单词表",
            SegmentStrategy = SegmentTypes.WordEntry
        };

        _llmMock.Setup(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        _llmMock.Setup(l => l.SegmentTextAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("LLM 服务不可用"));

        var service = CreateService(_llmMock.Object);
        var docxBytes = CreateMinimalDocx("abandon v. 放弃\nability n. 能力\nacademy n. 学院");
        using var stream = new MemoryStream(docxBytes);

        // Act & Assert: 必须抛 InvalidOperationException，异常消息说明 word_entry 策略不支持回退
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ParseAsync(stream, "word"));
        Assert.Contains("word_entry", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParseAsync_ConceptLlmFails_ThrowsAndDoesNotCreateBigRecord()
    {
        // Arrange: concept profile, LLM throws
        var profile = new DocumentProfile
        {
            Subject = "数学",
            DocType = "教材",
            SegmentStrategy = SegmentTypes.Concept
        };

        _llmMock.Setup(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        _llmMock.Setup(l => l.SegmentTextAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("LLM 服务不可用"));

        var service = CreateService(_llmMock.Object);
        var pdfBytes = CreateMinimalPdf("牛顿第二定律 F=ma。");
        using var stream = new MemoryStream(pdfBytes);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ParseAsync(stream, "pdf"));
        Assert.Contains("concept", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParseAsync_SentenceLlmFails_FallsBackToRuleBased()
    {
        // Arrange: sentence profile, LLM 抛异常 —— 必须保留旧行为回退到 SplitSentences
        var profile = new DocumentProfile
        {
            Subject = "English",
            DocType = "教材",
            SegmentStrategy = SegmentTypes.Sentence
        };

        _llmMock.Setup(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        _llmMock.Setup(l => l.SegmentTextAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("LLM 服务不可用"));

        var service = CreateService(_llmMock.Object);
        var pdfBytes = CreateMinimalPdf("Hello world. This is a test.");
        using var stream = new MemoryStream(pdfBytes);

        // Act: 不应抛异常
        var result = await service.ParseAsync(stream, "pdf");

        // Assert: 至少应该有 1 个 segment（来自 SplitSentences 回退）
        Assert.NotNull(result);
        var totalSegments = result.Pages.SelectMany(p => p.Segments).Count();
        Assert.True(totalSegments > 0, "sentence 策略下 LLM 失败仍应回退到规则切割产生 segments");
    }

    [Fact]
    public async Task ParseAsync_LlmReturnsCoverAllSegment_DiscardsIt()
    {
        // Arrange: LLM 返回一个 text == chunkText 的"摘要"型 segment
        var profile = new DocumentProfile
        {
            Subject = "English",
            DocType = "教材",
            SegmentStrategy = SegmentTypes.Sentence
        };

        var chunkText = "abandon v. 放弃\nability n. 能力";
        var segments = new List<SegmentResult>
        {
            // 正常的 word_entry segment
            new() { Text = "abandon v. 放弃", StartOffset = 0, EndOffset = 15, SegmentType = SegmentTypes.WordEntry },
            // 异常：覆盖整个 chunk 的"摘要"型 segment
            new() { Text = chunkText, StartOffset = 0, EndOffset = chunkText.Length, SegmentType = SegmentTypes.Sentence },
            new() { Text = "ability n. 能力", StartOffset = 16, EndOffset = 31, SegmentType = SegmentTypes.WordEntry }
        };

        _llmMock.Setup(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        _llmMock.Setup(l => l.SegmentTextAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(segments);

        var service = CreateService(_llmMock.Object);
        var docxBytes = CreateMinimalDocx(chunkText);
        using var stream = new MemoryStream(docxBytes);

        // Act
        var result = await service.ParseAsync(stream, "word");

        // Assert: 整块 segment 已被丢弃，剩下 2 个正常 segment
        var allSegments = result.Pages.SelectMany(p => p.Segments).ToList();
        Assert.Equal(2, allSegments.Count);
        Assert.DoesNotContain(allSegments, s => s.Text == chunkText);
    }

    [Fact]
    public async Task ParseAsync_WithoutLlm_WordEntryProfile_Throws()
    {
        // Arrange: 无 LLM 服务 + word_entry profile（模拟"用户上传单词表但没配 LLM"）
        // 这里通过 mock 一个 LLM 但 ChunkSize=0 模拟"LLM 配了但不可用"
        var profile = new DocumentProfile
        {
            Subject = "English",
            DocType = "单词表",
            SegmentStrategy = SegmentTypes.WordEntry
        };

        _llmMock.Setup(l => l.ChunkSize).Returns(0); // LLM 不可用
        _llmMock.Setup(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);

        var service = CreateService(_llmMock.Object);
        var docxBytes = CreateMinimalDocx("abandon v. 放弃");
        using var stream = new MemoryStream(docxBytes);

        // Act & Assert: 应在早检查阶段抛异常
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ParseAsync(stream, "word"));
        Assert.Contains("word_entry", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Token Generation Tests

    [Fact]
    public async Task ParseAsync_WithLlm_StillGeneratesTokens()
    {
        // Arrange
        var profile = new DocumentProfile
        {
            Subject = "English",
            DocType = "教材",
            SegmentStrategy = SegmentTypes.Sentence
        };

        var segments = new List<SegmentResult>
        {
            new() { Text = "The students were running.", StartOffset = 0, EndOffset = 26, SegmentType = SegmentTypes.Sentence }
        };

        _llmMock.Setup(l => l.AnalyzeDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        _llmMock.Setup(l => l.SegmentTextAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(segments);

        var service = CreateService(_llmMock.Object);
        var pdfBytes = CreateMinimalPdf("The students were running.");
        using var stream = new MemoryStream(pdfBytes);

        // Act
        var result = await service.ParseAsync(stream, "pdf");

        // Assert
        Assert.NotNull(result);
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
            }
        }
    }

    #endregion

    #region Helper Methods

    private DocumentParserService CreateService(ILlmSegmentationService? llmService = null)
    {
        return new DocumentParserService(_loggerMock.Object, llmService);
    }

    private static byte[] CreateMinimalPdf(string content)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        var content1 = "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n";
        var content2 = "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n";

        var escapedContent = content
            .Replace("\\", "\\\\")
            .Replace("(" , "\\(")
            .Replace(")", "\\)");
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
