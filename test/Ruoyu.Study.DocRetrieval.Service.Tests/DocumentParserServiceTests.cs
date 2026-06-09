using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Service;

namespace Ruoyu.Study.DocRetrieval.Service.Tests;

public class DocumentParserServiceTests
{
    private readonly DocumentParserService _service;

    public DocumentParserServiceTests()
    {
        var loggerMock = new Mock<ILogger<DocumentParserService>>();
        _service = new DocumentParserService(loggerMock.Object);
    }

    // UT-P-05: 不支持的文件类型抛出 NotSupportedException
    [Fact]
    public async Task ParseAsync_UnsupportedType_ShouldThrowNotSupportedException()
    {
        using var stream = new System.IO.MemoryStream();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => _service.ParseAsync(stream, "xlsx"));
        Assert.Contains("不支持的文件类型", ex.Message);
    }

    // UT-P-08: Token 分词只保留英文单词
    [Fact]
    public void Tokenize_ShouldOnlyKeepEnglishWords()
    {
        var tokens = DocumentParserService.Tokenize("Hello 123 World!");
        Assert.Equal(2, tokens.Count);
        Assert.Equal("Hello", tokens[0].TokenText);
        Assert.Equal("World", tokens[1].TokenText);
    }

    [Fact]
    public void Tokenize_ShouldPreserveOffsets()
    {
        var tokens = DocumentParserService.Tokenize("ab cd");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(0, tokens[0].StartOffset);
        Assert.Equal(2, tokens[0].EndOffset); // "ab"
        Assert.Equal(3, tokens[1].StartOffset);
        Assert.Equal(5, tokens[1].EndOffset); // "cd"
    }

    [Fact]
    public void Tokenize_EmptyText_ShouldReturnEmpty()
    {
        var tokens = DocumentParserService.Tokenize("");
        Assert.Empty(tokens);
    }

    // UT-P-09: Porter 词干还原正确
    [Fact]
    public void PorterStem_Running_ShouldReturnRun()
    {
        Assert.Equal("run", DocumentParserService.PorterStem("running"));
    }

    [Fact]
    public void PorterStem_Cats_ShouldReturnCat()
    {
        Assert.Equal("cat", DocumentParserService.PorterStem("cats"));
    }

    [Fact]
    public void PorterStem_Happiness_ShouldReturnHappi()
    {
        var result = DocumentParserService.PorterStem("happiness");
        // Porter stemmer should reduce "happiness" → "happi"
        Assert.Equal("happi", result);
    }

    [Fact]
    public void PorterStem_ShortWord_ShouldReturnSame()
    {
        Assert.Equal("ab", DocumentParserService.PorterStem("ab"));
    }

    // UT-P-10: 句子边界识别排除缩写
    [Fact]
    public void SplitSentences_ShouldNotSplitOnAbbreviations()
    {
        var sentences = _service.SplitSentences("Mr. Smith went to Dr. Jones.");
        Assert.Single(sentences);
        Assert.Contains("Mr.", sentences[0]);
        Assert.Contains("Dr.", sentences[0]);
    }

    [Fact]
    public void SplitSentences_ShouldSplitOnSentenceEnd()
    {
        var sentences = _service.SplitSentences("Hello world. This is a test.");
        Assert.Equal(2, sentences.Count);
        Assert.Equal("Hello world.", sentences[0]);
        Assert.Equal("This is a test.", sentences[1]);
    }

    [Fact]
    public void SplitSentences_ShouldHandleExclamationAndQuestion()
    {
        var sentences = _service.SplitSentences("Wow! Really? Yes.");
        Assert.Equal(3, sentences.Count);
        Assert.Equal("Wow!", sentences[0]);
        Assert.Equal("Really?", sentences[1]);
        Assert.Equal("Yes.", sentences[2]);
    }

    [Fact]
    public void SplitSentences_ShouldNotSplitOnDecimalPoint()
    {
        var sentences = _service.SplitSentences("The value is 3.14. That is pi.");
        Assert.Equal(2, sentences.Count);
        Assert.Contains("3.14", sentences[0]);
        Assert.Equal("That is pi.", sentences[1]);
    }

    // UT-P-11: 题目边界识别正确
    [Fact]
    public void ExtractQuestions_ShouldExtractNumberedQuestions()
    {
        var globalOffset = 0;
        var questions = _service.ExtractQuestions("1. What is the capital of France?\nA. Paris\nB. London\n2. What is 2+2?\nA. 3\nB. 4", "p1-b1", ref globalOffset);
        Assert.Equal(2, questions.Count);
        Assert.Equal("q1", questions[0].QuestionId);
        Assert.Contains("capital of France", questions[0].Stem);
        Assert.NotNull(questions[0].OptionsJson);
        Assert.Contains("Paris", questions[0].OptionsJson);
        Assert.Equal("q2", questions[1].QuestionId);
        Assert.True(questions[1].StartOffset > 0); // offset after first question's lines
    }

    [Fact]
    public void ExtractQuestions_ShouldHandleQuestionWithParentheses()
    {
        var globalOffset = 0;
        var questions = _service.ExtractQuestions("1) Choose the answer.\nA) Option A\nB) Option B", "p1-b1", ref globalOffset);
        Assert.Single(questions);
        Assert.Equal("q1", questions[0].QuestionId);
        Assert.NotNull(questions[0].OptionsJson);
        Assert.Contains("Option A", questions[0].OptionsJson);
    }

    // UT-P-12: OCR 后处理合并连字符打断的单词
    [Fact]
    public void OcrPostProcess_ShouldMergeHyphenatedWords()
    {
        var result = DocumentParserService.OcrPostProcess("word-\nword");
        Assert.Equal("wordword", result);
    }

    [Fact]
    public void OcrPostProcess_ShouldFixCommonOcrErrors()
    {
        var result = DocumentParserService.OcrPostProcess("A0 B0A");
        Assert.Equal("AO BOA", result);
    }

    // BT-06: 取消令牌
    [Fact]
    public async Task ParseAsync_CancellationToken_ShouldThrowOperationCanceled()
    {
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();

        // 使用一个有效的 1 页最小 PDF，确保取消令牌在页面遍历时触发
        var pdfBytes = CreateMinimalPdf();
        using var stream = new System.IO.MemoryStream(pdfBytes);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.ParseAsync(stream, "pdf", cts.Token));
    }

    /// <summary>
    /// 生成一个最小的合法 PDF 文件（1 页），用于测试
    /// </summary>
    private static byte[] CreateMinimalPdf()
    {
        var pdf = @"%PDF-1.7
1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj
2 0 obj
<< /Type /Pages /Kids [3 0 R] /Count 1 >>
endobj
3 0 obj
<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>
endobj
xref
0 4
0000000000 65535 f 
0000000009 00000 n 
0000000058 00000 n 
0000000115 00000 n 
trailer
<< /Size 4 /Root 1 0 R >>
startxref
190
%%EOF";
        return System.Text.Encoding.ASCII.GetBytes(pdf);
    }

    [Fact]
    public void SplitSentences_EmptyText_ShouldReturnEmpty()
    {
        var sentences = _service.SplitSentences("");
        Assert.Empty(sentences);
    }

    [Fact]
    public void ExtractQuestions_NoQuestions_ShouldReturnEmpty()
    {
        var globalOffset = 0;
        var questions = _service.ExtractQuestions("This is just text without any questions.", "p1-b1", ref globalOffset);
        Assert.Empty(questions);
    }
}