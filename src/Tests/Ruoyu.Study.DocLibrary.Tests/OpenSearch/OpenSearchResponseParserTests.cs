using System;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Service.OpenSearch;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests.OpenSearch;

public class OpenSearchResponseParserTests
{
    // ==================== ParseSearchResponse — Normal Cases ====================

    [Fact]
    public void ParseSearchResponse_WithValidJson_ReturnsResultsWithBlockFields()
    {
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 2 },
                ""hits"": [
                    {
                        ""_score"": 1.5,
                        ""_source"": {
                            ""file_name"": ""lecture.pdf"",
                            ""page_number"": 1,
                            ""text"": ""Hello world"",
                            ""block_id"": ""blk-001"",
                            ""block_type"": ""text"",
                            ""sort_index"": 0
                        },
                        ""sort"": [1.5, ""blk-001""]
                    },
                    {
                        ""_score"": 0.8,
                        ""_source"": {
                            ""file_name"": ""lecture.pdf"",
                            ""page_number"": 2,
                            ""text"": ""What is the capital?"",
                            ""block_id"": ""blk-002"",
                            ""block_type"": ""text"",
                            ""sort_index"": 1
                        },
                        ""sort"": [0.8, ""blk-002""]
                    }
                ]
            }
        }";

        var (results, totalCount, _) = OpenSearchResponseParser.ParseSearchResponse(
            responseJson, phrase: false, pageSize: 10);

        totalCount.Should().Be(2);
        results.Should().HaveCount(2);

        results[0].DocumentName.Should().Be("lecture.pdf");
        results[0].PageNumber.Should().Be(1);
        results[0].AssociatedText.Should().Be("Hello world");
        results[0].Score.Should().Be(1.5);
        results[0].MatchType.Should().Be(SearchMatchType.Stemmed);
        results[0].SegmentId.Should().Be("blk-001");
        // blocks pipeline has no offsets
        results[0].StartOffset.Should().Be(0);
        results[0].EndOffset.Should().Be(0);

        results[1].DocumentName.Should().Be("lecture.pdf");
        results[1].PageNumber.Should().Be(2);
        results[1].AssociatedText.Should().Be("What is the capital?");
        results[1].MatchType.Should().Be(SearchMatchType.Stemmed);
        results[1].SegmentId.Should().Be("blk-002");
    }

    [Fact]
    public void ParseSearchResponse_WithPhraseQuery_SetsExactPhraseMatchType()
    {
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 1 },
                ""hits"": [
                    {
                        ""_score"": 2.0,
                        ""_source"": {
                            ""file_name"": ""exam.pdf"",
                            ""page_number"": 3,
                            ""text"": ""Hello world"",
                            ""block_id"": ""blk-001"",
                            ""block_type"": ""text""
                        },
                        ""sort"": [2.0, ""blk-001""]
                    }
                ]
            }
        }";

        var (results, _, _) = OpenSearchResponseParser.ParseSearchResponse(
            responseJson, phrase: true, pageSize: 10);

        results.Should().HaveCount(1);
        results[0].MatchType.Should().Be(SearchMatchType.ExactPhrase);
    }

    // ==================== ParseSearchResponse — Highlight ====================

    [Fact]
    public void ParseSearchResponse_WithHighlight_UsesHighlightedText()
    {
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 1 },
                ""hits"": [
                    {
                        ""_score"": 1.0,
                        ""_source"": {
                            ""file_name"": ""test.pdf"",
                            ""page_number"": 1,
                            ""text"": ""Hello world"",
                            ""block_id"": ""blk-001""
                        },
                        ""highlight"": {
                            ""text"": [""<em>Hello</em> world""]
                        },
                        ""sort"": [1.0, ""blk-001""]
                    }
                ]
            }
        }";

        var (results, _, _) = OpenSearchResponseParser.ParseSearchResponse(
            responseJson, phrase: false, pageSize: 10);

        results.Should().HaveCount(1);
        results[0].AssociatedText.Should().Be("<em>Hello</em> world");
    }

    // ==================== ParseSearchResponse — Pagination Token ====================

    [Fact]
    public void ParseSearchResponse_WithFullPage_ReturnsNextToken()
    {
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 20 },
                ""hits"": [
                    {
                        ""_score"": 1.0,
                        ""_source"": {
                            ""file_name"": ""test.pdf"",
                            ""page_number"": 1,
                            ""text"": ""Hello"",
                            ""block_id"": ""blk-001""
                        },
                        ""sort"": [1.0, ""blk-001""]
                    }
                ]
            }
        }";

        var (_, _, nextToken) = OpenSearchResponseParser.ParseSearchResponse(
            responseJson, phrase: false, pageSize: 1);

        nextToken.Should().NotBeNull();
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(nextToken!));
        decoded.Should().Contain("1");
        decoded.Should().Contain("blk-001");
    }

    [Fact]
    public void ParseSearchResponse_WithPartialPage_ReturnsNoNextToken()
    {
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 1 },
                ""hits"": [
                    {
                        ""_score"": 1.0,
                        ""_source"": {
                            ""file_name"": ""test.pdf"",
                            ""page_number"": 1,
                            ""text"": ""Hello"",
                            ""block_id"": ""blk-001""
                        },
                        ""sort"": [1.0, ""blk-001""]
                    }
                ]
            }
        }";

        var (_, _, nextToken) = OpenSearchResponseParser.ParseSearchResponse(
            responseJson, phrase: false, pageSize: 10);

        nextToken.Should().BeNull();
    }

    // ==================== ParseSearchResponse — Edge Cases ====================

    [Fact]
    public void ParseSearchResponse_WithEmptyHits_ReturnsEmptyResults()
    {
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 0 },
                ""hits"": []
            }
        }";

        var (results, totalCount, nextToken) = OpenSearchResponseParser.ParseSearchResponse(
            responseJson, phrase: false, pageSize: 10);

        results.Should().BeEmpty();
        totalCount.Should().Be(0);
        nextToken.Should().BeNull();
    }

    [Fact]
    public void ParseSearchResponse_WithMissingFields_UsesDefaults()
    {
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 1 },
                ""hits"": [
                    {
                        ""_source"": {},
                        ""sort"": [1.0, ""blk-001""]
                    }
                ]
            }
        }";

        var (results, _, _) = OpenSearchResponseParser.ParseSearchResponse(
            responseJson, phrase: false, pageSize: 1);

        results.Should().HaveCount(1);
        results[0].DocumentName.Should().BeEmpty();
        results[0].PageNumber.Should().Be(0);
        results[0].AssociatedText.Should().BeEmpty();
        results[0].SegmentId.Should().BeEmpty();
        results[0].MatchType.Should().Be(SearchMatchType.Stemmed);
    }

    [Fact]
    public void ParseSearchResponse_WithMissingScore_DefaultsToZero()
    {
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 1 },
                ""hits"": [
                    {
                        ""_source"": {
                            ""file_name"": ""test.pdf"",
                            ""page_number"": 1,
                            ""text"": ""Hello"",
                            ""block_id"": ""blk-001""
                        },
                        ""sort"": [1.0, ""blk-001""]
                    }
                ]
            }
        }";

        var (results, _, _) = OpenSearchResponseParser.ParseSearchResponse(
            responseJson, phrase: false, pageSize: 10);

        results.Should().HaveCount(1);
        results[0].Score.Should().Be(0);
    }

    [Fact]
    public void ParseSearchResponse_WithNoHitsProperty_ReturnsEmptyResults()
    {
        // Response without "hits" at root level — tests the hasHits guard
        var responseJson = @"{ ""error"": ""something"" }";

        var (results, totalCount, nextToken) = OpenSearchResponseParser.ParseSearchResponse(
            responseJson, phrase: false, pageSize: 10);

        results.Should().BeEmpty();
        totalCount.Should().Be(0);
        nextToken.Should().BeNull();
    }

    // ==================== [Gen-2] ParseSearchResponse — minerU field mapping ====================

    [Fact]
    public void ParseSearchResponse_WithBlockData_ReturnsBlockResultFields()
    {
        var blockDataRaw = "{\"type\":\"text\",\"text\":\"hello\"}";
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 1 },
                ""hits"": [
                    {
                        ""_score"": 1.5,
                        ""_source"": {
                            ""file_name"": ""doc.pdf"",
                            ""page_number"": 2,
                            ""text"": ""content"",
                            ""block_id"": ""blk-001"",
                            ""_meta"": { ""block_data"": """ + blockDataRaw.Replace("\"", "\\\"") + @""" },
                            ""sub_type"": ""image_caption"",
                            ""text_level"": 1,
                            ""text_format"": ""markdown"",
                            ""caption"": ""Figure 1"",
                            ""score"": 0.95
                        },
                        ""sort"": [1.5, ""blk-001""]
                    }
                ]
            }
        }";

        var (results, _, _) = OpenSearchResponseParser.ParseSearchResponse(
            responseJson, phrase: false, pageSize: 10);

        results.Should().HaveCount(1);
        results[0].BlockData.Should().Be(blockDataRaw);
        results[0].SubType.Should().Be("image_caption");
        results[0].TextLevel.Should().Be(1);
        results[0].TextFormat.Should().Be("markdown");
        results[0].Caption.Should().Be("Figure 1");
        results[0].MineruScore.Should().Be(0.95);
    }

    [Fact]
    public void ParseSearchResponse_BboxAndScoreFields_MappedCorrectly()
    {
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 1 },
                ""hits"": [
                    {
                        ""_score"": 2.0,
                        ""_source"": {
                            ""file_name"": ""doc.pdf"",
                            ""page_number"": 1,
                            ""text"": ""text"",
                            ""block_id"": ""blk-001"",
                            ""x0"": 100.5,
                            ""y0"": 200.0,
                            ""x1"": 300.25,
                            ""y1"": 400.0,
                            ""score"": 0.88
                        },
                        ""sort"": [2.0, ""blk-001""]
                    }
                ]
            }
        }";

        var (results, _, _) = OpenSearchResponseParser.ParseSearchResponse(
            responseJson, phrase: false, pageSize: 10);

        results.Should().HaveCount(1);
        results[0].Bbox.Should().NotBeNull();
        results[0].Bbox!.Should().HaveCount(4);
        results[0].Bbox![0].Should().Be(100.5f);
        results[0].Bbox![1].Should().Be(200.0f);
        results[0].Bbox![2].Should().Be(300.25f);
        results[0].Bbox![3].Should().Be(400.0f);
        results[0].MineruScore.Should().Be(0.88);
        // V1 Score (OpenSearch _score) should still be set correctly
        results[0].Score.Should().Be(2.0);
    }

    [Fact]
    public void ParseSearchResponse_WithMissingMinerUFields_FallsBackToNull()
    {
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 1 },
                ""hits"": [
                    {
                        ""_score"": 1.0,
                        ""_source"": {
                            ""file_name"": ""doc.pdf"",
                            ""page_number"": 1,
                            ""text"": ""text"",
                            ""block_id"": ""blk-001""
                        },
                        ""sort"": [1.0, ""blk-001""]
                    }
                ]
            }
        }";

        var (results, _, _) = OpenSearchResponseParser.ParseSearchResponse(
            responseJson, phrase: false, pageSize: 10);

        results.Should().HaveCount(1);
        results[0].BlockData.Should().BeNull();
        results[0].Bbox.Should().BeNull();
        results[0].MineruScore.Should().BeNull();
        results[0].SubType.Should().BeNull();
        results[0].TextLevel.Should().BeNull();
        results[0].TextFormat.Should().BeNull();
        results[0].Caption.Should().BeNull();
        // V1 fields still work
        results[0].DocumentName.Should().Be("doc.pdf");
        results[0].SegmentId.Should().Be("blk-001");
    }

    [Fact]
    public void ParseSearchResponse_WithPartialBbox_StillConstructsArray()
    {
        // Only x0 and y1 present — bbox should still be constructed with 0 for missing components
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 1 },
                ""hits"": [
                    {
                        ""_source"": {
                            ""file_name"": ""doc.pdf"",
                            ""page_number"": 1,
                            ""text"": ""text"",
                            ""block_id"": ""blk-001"",
                            ""x0"": 50.0,
                            ""y1"": 600.0
                        },
                        ""sort"": [1.0, ""blk-001""]
                    }
                ]
            }
        }";

        var (results, _, _) = OpenSearchResponseParser.ParseSearchResponse(
            responseJson, phrase: false, pageSize: 10);

        results.Should().HaveCount(1);
        results[0].Bbox.Should().NotBeNull();
        results[0].Bbox!.Should().HaveCount(4);
        results[0].Bbox![0].Should().Be(50.0f);
        results[0].Bbox![1].Should().Be(0f);  // y0 missing → 0
        results[0].Bbox![2].Should().Be(0f);  // x1 missing → 0
        results[0].Bbox![3].Should().Be(600.0f);
    }
}
