using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Service;
using Xunit;

namespace Ruoyu.Study.DocRetrieval.Tests;

public class LlmSegmentationServiceTests
{
    private readonly Mock<ILogger<LlmSegmentationService>> _loggerMock;

    public LlmSegmentationServiceTests()
    {
        _loggerMock = new Mock<ILogger<LlmSegmentationService>>();
    }

    #region AnalyzeDocumentAsync Tests

    [Fact]
    public async Task AnalyzeDocumentAsync_EmptyText_ReturnsDefaultProfile()
    {
        // Arrange
        var service = CreateService();
        var text = "";

        // Act
        var result = await service.AnalyzeDocumentAsync(text);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("其他", result.Subject);
        Assert.Equal("其他", result.DocType);
        Assert.Equal(SegmentTypes.Sentence, result.SegmentStrategy);
    }

    [Fact]
    public async Task AnalyzeDocumentAsync_EnglishTextbook_ReturnsCorrectProfile()
    {
        // Arrange
        var llmResponse = """
        {
            "subject": "English",
            "doc_type": "教材",
            "segment_strategy": "sentence",
            "structure": {
                "has_chapters": true,
                "has_questions": false,
                "has_word_list": false,
                "has_formulas": false
            }
        }
        """;

        var service = CreateService(llmResponse);
        var text = "Chapter 1: Introduction to English Grammar. This textbook covers basic grammar rules.";

        // Act
        var result = await service.AnalyzeDocumentAsync(text);

        // Assert
        Assert.Equal("English", result.Subject);
        Assert.Equal("教材", result.DocType);
        Assert.Equal(SegmentTypes.Sentence, result.SegmentStrategy);
        Assert.True(result.Structure.HasChapters);
    }

    [Fact]
    public async Task AnalyzeDocumentAsync_MathExam_ReturnsQuestionStrategy()
    {
        // Arrange
        var llmResponse = """
        {
            "subject": "数学",
            "doc_type": "试卷",
            "segment_strategy": "question",
            "structure": {
                "has_chapters": false,
                "has_questions": true,
                "has_word_list": false,
                "has_formulas": true
            }
        }
        """;

        var service = CreateService(llmResponse);
        var text = "1. 计算 3x + 5 = 20 的解。A. x=3 B. x=5 C. x=7 D. x=10";

        // Act
        var result = await service.AnalyzeDocumentAsync(text);

        // Assert
        Assert.Equal("数学", result.Subject);
        Assert.Equal("试卷", result.DocType);
        Assert.Equal(SegmentTypes.Question, result.SegmentStrategy);
        Assert.True(result.Structure.HasQuestions);
        Assert.True(result.Structure.HasFormulas);
    }

    [Fact]
    public async Task AnalyzeDocumentAsync_WordList_ReturnsWordEntryStrategy()
    {
        // Arrange
        var llmResponse = """
        {
            "subject": "English",
            "doc_type": "单词表",
            "segment_strategy": "word_entry",
            "structure": {
                "has_chapters": false,
                "has_questions": false,
                "has_word_list": true,
                "has_formulas": false
            }
        }
        """;

        var service = CreateService(llmResponse);
        var text = "abandon /əˈbændən/ v. 放弃\nability /əˈbɪləti/ n. 能力";

        // Act
        var result = await service.AnalyzeDocumentAsync(text);

        // Assert
        Assert.Equal("English", result.Subject);
        Assert.Equal("单词表", result.DocType);
        Assert.Equal(SegmentTypes.WordEntry, result.SegmentStrategy);
        Assert.True(result.Structure.HasWordList);
    }

    [Fact]
    public async Task AnalyzeDocumentAsync_LlmReturnsMarkdownJson_ParsesCorrectly()
    {
        // Arrange - LLM returns JSON wrapped in markdown code block
        var llmResponse = """
        ```json
        {
            "subject": "物理",
            "doc_type": "教材",
            "segment_strategy": "concept",
            "structure": {
                "has_chapters": true,
                "has_questions": false,
                "has_word_list": false,
                "has_formulas": true
            }
        }
        ```
        """;

        var service = CreateService(llmResponse);
        var text = "牛顿第一定律：一切物体在没有受到外力作用的时候，总保持匀速直线运动状态或静止状态。";

        // Act
        var result = await service.AnalyzeDocumentAsync(text);

        // Assert
        Assert.Equal("物理", result.Subject);
        Assert.Equal(SegmentTypes.Concept, result.SegmentStrategy);
    }

    [Fact]
    public async Task AnalyzeDocumentAsync_LlmReturnsInvalidJson_ReturnsDefaultProfile()
    {
        // Arrange
        var service = CreateService("This is not valid JSON");
        var text = "Some text for analysis";

        // Act
        var result = await service.AnalyzeDocumentAsync(text);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("其他", result.Subject);
    }

    [Fact]
    public async Task AnalyzeDocumentAsync_LlmCallFails_ReturnsDefaultProfile()
    {
        // Arrange
        var service = CreateService(statusCode: HttpStatusCode.InternalServerError);
        var text = "Some text for analysis";

        // Act
        var result = await service.AnalyzeDocumentAsync(text);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("其他", result.Subject);
    }

    #endregion

    #region SegmentTextAsync Tests

    [Fact]
    public async Task SegmentTextAsync_EmptyText_ReturnsEmptyList()
    {
        // Arrange
        var service = CreateService();
        var profile = new DocumentProfile { SegmentStrategy = SegmentTypes.Sentence };

        // Act
        var result = await service.SegmentTextAsync("", profile);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task SegmentTextAsync_SentenceStrategy_ReturnsSentences()
    {
        // Arrange
        var llmResponse = """
        {
            "segments": [
                {"text": "The quick brown fox.", "start_offset": 0, "end_offset": 20, "segment_type": "sentence"},
                {"text": "Jumps over the lazy dog.", "start_offset": 21, "end_offset": 45, "segment_type": "sentence"}
            ]
        }
        """;

        var service = CreateService(llmResponse);
        var profile = new DocumentProfile { SegmentStrategy = SegmentTypes.Sentence };
        var text = "The quick brown fox. Jumps over the lazy dog.";

        // Act
        var result = await service.SegmentTextAsync(text, profile);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("The quick brown fox.", result[0].Text);
        Assert.Equal("Jumps over the lazy dog.", result[1].Text);
        Assert.Equal(SegmentTypes.Sentence, result[0].SegmentType);
    }

    [Fact]
    public async Task SegmentTextAsync_WordEntryStrategy_ReturnsEntries()
    {
        // Arrange
        var llmResponse = """
        {
            "segments": [
                {"text": "abandon /əˈbændən/ v. 放弃", "start_offset": 0, "end_offset": 28, "segment_type": "word_entry"},
                {"text": "ability /əˈbɪləti/ n. 能力", "start_offset": 29, "end_offset": 54, "segment_type": "word_entry"}
            ]
        }
        """;

        var service = CreateService(llmResponse);
        var profile = new DocumentProfile { SegmentStrategy = SegmentTypes.WordEntry };
        var text = "abandon /əˈbændən/ v. 放弃\nability /əˈbɪləti/ n. 能力";

        // Act
        var result = await service.SegmentTextAsync(text, profile);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal(SegmentTypes.WordEntry, result[0].SegmentType);
        Assert.Contains("abandon", result[0].Text);
        Assert.Contains("ability", result[1].Text);
    }

    [Fact]
    public async Task SegmentTextAsync_QuestionStrategy_ReturnsQuestions()
    {
        // Arrange
        var llmResponse = """
        {
            "segments": [
                {"text": "1. What is 2+2? A. 3 B. 4 C. 5 D. 6", "start_offset": 0, "end_offset": 36, "segment_type": "question"},
                {"text": "2. What is 3+3? A. 5 B. 6 C. 7 D. 8", "start_offset": 37, "end_offset": 73, "segment_type": "question"}
            ]
        }
        """;

        var service = CreateService(llmResponse);
        var profile = new DocumentProfile { SegmentStrategy = SegmentTypes.Question };
        var text = "1. What is 2+2? A. 3 B. 4 C. 5 D. 6\n2. What is 3+3? A. 5 B. 6 C. 7 D. 8";

        // Act
        var result = await service.SegmentTextAsync(text, profile);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal(SegmentTypes.Question, result[0].SegmentType);
    }

    [Fact]
    public async Task SegmentTextAsync_LlmReturnsEmptySegments_ReturnsEmptyList()
    {
        // Arrange
        var llmResponse = """{"segments": []}""";
        var service = CreateService(llmResponse);
        var profile = new DocumentProfile { SegmentStrategy = SegmentTypes.Sentence };
        var text = "Some text";

        // Act
        var result = await service.SegmentTextAsync(text, profile);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task SegmentTextAsync_LlmReturnsInvalidJson_ReturnsEmptyList()
    {
        // Arrange
        var service = CreateService("Not valid JSON");
        var profile = new DocumentProfile { SegmentStrategy = SegmentTypes.Sentence };
        var text = "Some text";

        // Act
        var result = await service.SegmentTextAsync(text, profile);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task SegmentTextAsync_LlmCallFails_ThrowsException()
    {
        // Arrange
        var service = CreateService(statusCode: HttpStatusCode.InternalServerError);
        var profile = new DocumentProfile { SegmentStrategy = SegmentTypes.Sentence };
        var text = "Some text";

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.SegmentTextAsync(text, profile));
    }

    [Fact]
    public async Task SegmentTextAsync_OffsetsNeedFixing_FixesFromOriginalText()
    {
        // Arrange - LLM returns segments with invalid offsets
        var llmResponse = """
        {
            "segments": [
                {"text": "Hello world.", "start_offset": -1, "end_offset": -1, "segment_type": "sentence"},
                {"text": "How are you?", "start_offset": 0, "end_offset": 0, "segment_type": "sentence"}
            ]
        }
        """;

        var service = CreateService(llmResponse);
        var profile = new DocumentProfile { SegmentStrategy = SegmentTypes.Sentence };
        var text = "Hello world. How are you?";

        // Act
        var result = await service.SegmentTextAsync(text, profile);

        // Assert
        Assert.Equal(2, result.Count);
        // Should have found the correct offsets
        Assert.Equal(0, result[0].StartOffset);
        Assert.Equal(12, result[0].EndOffset);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task AnalyzeDocumentAsync_VeryLongText_TruncatesTo2000Chars()
    {
        // Arrange
        var llmResponse = """
        {
            "subject": "English",
            "doc_type": "教材",
            "segment_strategy": "sentence",
            "structure": {"has_chapters": false, "has_questions": false, "has_word_list": false, "has_formulas": false}
        }
        """;

        var service = CreateService(llmResponse);
        var text = new string('A', 5000); // 5000 chars

        // Act
        var result = await service.AnalyzeDocumentAsync(text);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("English", result.Subject);
    }

    [Fact]
    public async Task SegmentTextAsync_MarkdownWrappedJson_ParsesCorrectly()
    {
        // Arrange
        var llmResponse = """
        ```json
        {
            "segments": [
                {"text": "Test sentence.", "start_offset": 0, "end_offset": 14, "segment_type": "sentence"}
            ]
        }
        ```
        """;

        var service = CreateService(llmResponse);
        var profile = new DocumentProfile { SegmentStrategy = SegmentTypes.Sentence };

        // Act
        var result = await service.SegmentTextAsync("Test sentence.", profile);

        // Assert
        Assert.Single(result);
        Assert.Equal("Test sentence.", result[0].Text);
    }

    #endregion

    #region Helper Methods

    private LlmSegmentationService CreateService(
        string? llmResponse = null,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handlerMock = new Mock<HttpMessageHandler>();

        // Mock /models endpoint for initialization
        var modelResponse = new HttpResponseMessage(HttpStatusCode.OK);
        var modelInfo = new { id = "gpt-4o-mini", context_length = 32768 };
        modelResponse.Content = new StringContent(
            JsonSerializer.Serialize(modelInfo),
            System.Text.Encoding.UTF8,
            "application/json");

        // Mock /chat/completions endpoint — SSE streaming format
        var chatResponse = new HttpResponseMessage(statusCode);
        if (llmResponse != null && statusCode == HttpStatusCode.OK)
        {
            var sseBuilder = new System.Text.StringBuilder();
            var chunkSize = Math.Max(1, llmResponse.Length / 3);
            for (var i = 0; i < llmResponse.Length; i += chunkSize)
            {
                var chunk = llmResponse[i..Math.Min(i + chunkSize, llmResponse.Length)];
                var sseChunk = new { choices = new[] { new { delta = new { content = chunk } } } };
                sseBuilder.Append($"data: {JsonSerializer.Serialize(sseChunk)}\n\n");
            }
            sseBuilder.Append("data: [DONE]\n\n");

            chatResponse.Content = new StringContent(
                sseBuilder.ToString(),
                System.Text.Encoding.UTF8,
                "text/event-stream");
        }

        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.PathAndQuery.Contains("/models/")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(modelResponse);

        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.PathAndQuery.Contains("/chat/completions")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(chatResponse);

        var httpClient = new HttpClient(handlerMock.Object)
        {
            BaseAddress = new Uri("https://api.openai.com/v1")
        };

        var options = Options.Create(new LlmSegmentationOptions
        {
            Model = "gpt-4o-mini",
            ApiKey = "test-key",
            BaseUrl = "https://api.openai.com/v1",
            MaxTokens = "4K",
            ChunkSize = 2500
        });

        return new LlmSegmentationService(httpClient, options, _loggerMock.Object);
    }

    #endregion
}
