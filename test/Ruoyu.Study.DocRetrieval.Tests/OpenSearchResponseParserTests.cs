using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Xunit;

namespace Ruoyu.Study.DocRetrieval.Tests;

public class OpenSearchResponseParserTests
{
    [Fact]
    public void ParseSearchResponse_WithValidJson_ReturnsResults()
    {
        // Arrange
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 2 },
                ""hits"": [
                    {
                        ""_score"": 1.5,
                        ""_source"": {
                            ""document_title"": ""test.pdf"",
                            ""page_number"": 1,
                            ""text"": ""Hello world"",
                            ""segment_type"": ""sentence"",
                            ""sentence_id"": ""s1"",
                            ""start_offset"": 0,
                            ""end_offset"": 11
                        },
                        ""sort"": [1.5, ""doc1"", ""sentence""]
                    },
                    {
                        ""_score"": 0.8,
                        ""_source"": {
                            ""document_title"": ""test.pdf"",
                            ""page_number"": 2,
                            ""text"": ""What is the capital?"",
                            ""segment_type"": ""question"",
                            ""question_id"": ""q1"",
                            ""start_offset"": 0,
                            ""end_offset"": 22
                        },
                        ""sort"": [0.8, ""doc1"", ""question""]
                    }
                ]
            }
        }";

        // Act
        var (results, totalCount, nextToken) = ParseSearchResponse(responseJson, phrase: false, pageSize: 10);

        // Assert
        Assert.Equal(2, totalCount);
        Assert.Equal(2, results.Count);

        Assert.Equal("test.pdf", results[0].DocumentName);
        Assert.Equal(1, results[0].PageNumber);
        Assert.Equal("Hello world", results[0].AssociatedText);
        Assert.Equal(1.5, results[0].Score);
        Assert.Equal(SearchMatchType.Stemmed, results[0].MatchType);
        Assert.Equal("s1", results[0].SegmentId);

        Assert.Equal("test.pdf", results[1].DocumentName);
        Assert.Equal(2, results[1].PageNumber);
        Assert.Equal("What is the capital?", results[1].AssociatedText);
        Assert.Equal(SearchMatchType.Stemmed, results[1].MatchType);
        Assert.Equal("q1", results[1].SegmentId);
    }

    [Fact]
    public void ParseSearchResponse_WithPhraseQuery_SetsExactPhraseMatchType()
    {
        // Arrange
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 1 },
                ""hits"": [
                    {
                        ""_score"": 2.0,
                        ""_source"": {
                            ""document_title"": ""exam.pdf"",
                            ""page_number"": 3,
                            ""text"": ""Hello world"",
                            ""segment_type"": ""sentence"",
                            ""sentence_id"": ""s2"",
                            ""start_offset"": 0,
                            ""end_offset"": 11
                        },
                        ""sort"": [2.0, ""doc2"", ""sentence""]
                    }
                ]
            }
        }";

        // Act
        var (results, _, _) = ParseSearchResponse(responseJson, phrase: true, pageSize: 10);

        // Assert
        Assert.Single(results);
        Assert.Equal(SearchMatchType.ExactPhrase, results[0].MatchType);
    }

    [Fact]
    public void ParseSearchResponse_WithHighlight_UsesHighlightedText()
    {
        // Arrange
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 1 },
                ""hits"": [
                    {
                        ""_score"": 1.0,
                        ""_source"": {
                            ""document_title"": ""test.pdf"",
                            ""page_number"": 1,
                            ""text"": ""Hello world"",
                            ""segment_type"": ""sentence"",
                            ""sentence_id"": ""s1"",
                            ""start_offset"": 0,
                            ""end_offset"": 11
                        },
                        ""highlight"": {
                            ""text"": [""<em>Hello</em> world""]
                        },
                        ""sort"": [1.0, ""doc1"", ""sentence""]
                    }
                ]
            }
        }";

        // Act
        var (results, _, _) = ParseSearchResponse(responseJson, phrase: false, pageSize: 10);

        // Assert
        Assert.Single(results);
        Assert.Equal("<em>Hello</em> world", results[0].AssociatedText);
    }

    [Fact]
    public void ParseSearchResponse_WithFullPage_ReturnsNextToken()
    {
        // Arrange
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 20 },
                ""hits"": [
                    {
                        ""_score"": 1.0,
                        ""_source"": {
                            ""document_title"": ""test.pdf"",
                            ""page_number"": 1,
                            ""text"": ""Hello"",
                            ""segment_type"": ""sentence"",
                            ""sentence_id"": ""s1"",
                            ""start_offset"": 0,
                            ""end_offset"": 5
                        },
                        ""sort"": [1.0, ""doc1"", ""sentence""]
                    }
                ]
            }
        }";

        // Act
        var (_, _, nextToken) = ParseSearchResponse(responseJson, phrase: false, pageSize: 1);

        // Assert
        Assert.NotNull(nextToken);
    }

    [Fact]
    public void ParseSearchResponse_WithPartialPage_ReturnsNoNextToken()
    {
        // Arrange
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 1 },
                ""hits"": [
                    {
                        ""_score"": 1.0,
                        ""_source"": {
                            ""document_title"": ""test.pdf"",
                            ""page_number"": 1,
                            ""text"": ""Hello"",
                            ""segment_type"": ""sentence"",
                            ""sentence_id"": ""s1"",
                            ""start_offset"": 0,
                            ""end_offset"": 5
                        },
                        ""sort"": [1.0, ""doc1"", ""sentence""]
                    }
                ]
            }
        }";

        // Act
        var (_, _, nextToken) = ParseSearchResponse(responseJson, phrase: false, pageSize: 10);

        // Assert
        Assert.Null(nextToken);
    }

    [Fact]
    public void ParseSearchResponse_WithEmptyHits_ReturnsEmptyResults()
    {
        // Arrange
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 0 },
                ""hits"": []
            }
        }";

        // Act
        var (results, totalCount, nextToken) = ParseSearchResponse(responseJson, phrase: false, pageSize: 10);

        // Assert
        Assert.Empty(results);
        Assert.Equal(0, totalCount);
        Assert.Null(nextToken);
    }

    [Fact]
    public void ParseSearchResponse_WithMissingFields_UsesDefaults()
    {
        // Arrange
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 1 },
                ""hits"": [
                    {
                        ""_source"": {},
                        ""sort"": [1.0, ""doc1"", ""sentence""]
                    }
                ]
            }
        }";

        // Act
        var (results, _, _) = ParseSearchResponse(responseJson, phrase: false, pageSize: 1);

        // Assert
        Assert.Single(results);
        Assert.Equal(string.Empty, results[0].DocumentName);
        Assert.Equal(0, results[0].PageNumber);
        Assert.Equal(string.Empty, results[0].AssociatedText);
        Assert.Equal(string.Empty, results[0].SegmentId);
        Assert.Equal(SearchMatchType.Stemmed, results[0].MatchType);
    }

    /// <summary>
    /// Extracted parsing logic from OpenSearchIndexService.ExactSearchAsync for testability.
    /// This mirrors the JSON parsing logic in OpenSearchIndexService.
    /// </summary>
    private static (List<SearchResultModel> Results, int TotalCount, string? NextToken) ParseSearchResponse(
        string responseJson, bool phrase, int pageSize)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var totalCount = root.TryGetProperty("hits", out var hitsEl)
            && hitsEl.TryGetProperty("total", out var totalEl)
            && totalEl.TryGetProperty("value", out var valueEl)
            ? valueEl.GetInt32()
            : 0;

        var results = new List<SearchResultModel>();
        JsonElement lastSort = default;
        var hasLastSort = false;

        if (hitsEl.TryGetProperty("hits", out var hitArray))
        {
            foreach (var hit in hitArray.EnumerateArray())
            {
                var source = hit.GetProperty("_source");
                var segmentType = source.TryGetProperty("segment_type", out var stEl) ? stEl.GetString() ?? SegmentTypes.Sentence : SegmentTypes.Sentence;
                var segmentId = segmentType == SegmentTypes.Question
                    ? (source.TryGetProperty("question_id", out var qiEl) ? qiEl.GetString() ?? "" : "")
                    : (source.TryGetProperty("sentence_id", out var siEl) ? siEl.GetString() ?? "" : "");

                var associatedText = source.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";

                // Use highlighted text if available
                if (hit.TryGetProperty("highlight", out var highlightEl)
                    && highlightEl.TryGetProperty("text", out var highlightTexts))
                {
                    var firstHighlight = highlightTexts.EnumerateArray().FirstOrDefault();
                    if (firstHighlight.ValueKind != JsonValueKind.Undefined)
                        associatedText = firstHighlight.GetString() ?? associatedText;
                }

                var score = hit.TryGetProperty("_score", out var scoreEl) ? scoreEl.GetDouble() : 0;

                results.Add(new SearchResultModel
                {
                    DocumentName = source.TryGetProperty("document_title", out var dtEl) ? dtEl.GetString() ?? "" : "",
                    PageNumber = source.TryGetProperty("page_number", out var pnEl) ? pnEl.GetInt32() : 0,
                    AssociatedText = associatedText,
                    Score = score,
                    MatchType = phrase ? SearchMatchType.ExactPhrase : SearchMatchType.Stemmed,
                    SegmentId = segmentId,
                    StartOffset = source.TryGetProperty("start_offset", out var soEl) ? soEl.GetInt32() : 0,
                    EndOffset = source.TryGetProperty("end_offset", out var eoEl) ? eoEl.GetInt32() : 0
                });

                if (hit.TryGetProperty("sort", out var sortEl))
                {
                    lastSort = sortEl;
                    hasLastSort = true;
                }
            }
        }

        string? nextToken = null;
        if (hasLastSort && results.Count == pageSize)
        {
            nextToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(lastSort.GetRawText()));
        }

        return (results, totalCount, nextToken);
    }
}
