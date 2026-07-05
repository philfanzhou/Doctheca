using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using global::Ruoyu.Study.DocLibrary.Domain.Models;
using global::Ruoyu.Study.DocLibrary.Service;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

public class OpenSearchIndexServiceTests
{
    // ==================== BuildSearchBody — Query Construction ====================

    [Fact]
    public void BuildSearchBody_WithPhraseQuery_UsesMatchPhraseOnTextExact()
    {
        var body = OpenSearchIndexService.BuildSearchBody(
            "machine learning", phrase: true, filter: null, pageSize: 10, pageToken: null);

        var json = JsonSerializer.Serialize(body);
        json.Should().Contain("match_phrase");
        json.Should().Contain("text.exact");
        json.Should().NotContain("\"match\":");
    }

    [Fact]
    public void BuildSearchBody_WithStemmedQuery_UsesMatchOnText()
    {
        var body = OpenSearchIndexService.BuildSearchBody(
            "machine", phrase: false, filter: null, pageSize: 10, pageToken: null);

        var json = JsonSerializer.Serialize(body);
        json.Should().Contain("\"match\"");
        json.Should().Contain("\"text\"");
        json.Should().NotContain("match_phrase");
        json.Should().NotContain("text.exact");
    }

    // ==================== BuildSearchBody — Filter Construction ====================

    [Fact]
    public void BuildSearchBody_WithFullFilter_WrapsInBoolWithAllClauses()
    {
        var filter = new SearchFilterModel
        {
            Subject = "英语",
            Grade = "G10",
            Year = "2024",
            DocumentTitle = "exam.pdf"
        };

        var body = OpenSearchIndexService.BuildSearchBody(
            "test", phrase: false, filter, pageSize: 10, pageToken: null);

        var json = JsonSerializer.Serialize(body);
        json.Should().Contain("\"bool\"");
        json.Should().Contain("\"must\"");
        json.Should().Contain("\"filter\"");
        json.Should().Contain("\"subject\"");
        json.Should().Contain("\"grade\"");
        json.Should().Contain("\"year\"");
        // file_name is the actual indexed field (DocumentTitle filter maps to file_name)
        json.Should().Contain("\"file_name\"");
    }

    [Fact]
    public void BuildSearchBody_WithNullFilter_DoesNotWrapInBool()
    {
        var body = OpenSearchIndexService.BuildSearchBody(
            "test", phrase: false, filter: null, pageSize: 10, pageToken: null);

        var json = JsonSerializer.Serialize(body);
        json.Should().NotContain("\"bool\"");
    }

    [Fact]
    public void BuildSearchBody_WithPartialFilter_OnlyIncludesNonEmptyFields()
    {
        var filter = new SearchFilterModel
        {
            Subject = "英语",
            Grade = "",
            Year = null,
            DocumentTitle = ""
        };

        var body = OpenSearchIndexService.BuildSearchBody(
            "test", phrase: false, filter, pageSize: 10, pageToken: null);

        var json = JsonSerializer.Serialize(body);
        json.Should().Contain("\"bool\"");
        json.Should().Contain("\"subject\"");
        json.Should().NotContain("\"grade\"");
        json.Should().NotContain("\"year\"");
        json.Should().NotContain("\"file_name\"");
    }

    // ==================== BuildSearchBody — Page Token ====================

    [Fact]
    public void BuildSearchBody_WithValidPageToken_IncludesSearchAfter()
    {
        var sortArray = "[1.5, \"blk-abc\"]";
        var pageToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(sortArray));

        var body = OpenSearchIndexService.BuildSearchBody(
            "test", phrase: false, filter: null, pageSize: 10, pageToken);

        body.Should().ContainKey("search_after");
    }

    [Fact]
    public void BuildSearchBody_WithInvalidPageToken_DoesNotIncludeSearchAfter()
    {
        var body = OpenSearchIndexService.BuildSearchBody(
            "test", phrase: false, filter: null, pageSize: 10, pageToken: "!!!invalid base64!!!");

        body.Should().NotContainKey("search_after");
    }

    [Fact]
    public void BuildSearchBody_WithNullOrEmptyPageToken_DoesNotIncludeSearchAfter()
    {
        var body1 = OpenSearchIndexService.BuildSearchBody(
            "test", phrase: false, filter: null, pageSize: 10, pageToken: null);
        body1.Should().NotContainKey("search_after");

        var body2 = OpenSearchIndexService.BuildSearchBody(
            "test", phrase: false, filter: null, pageSize: 10, pageToken: "");
        body2.Should().NotContainKey("search_after");
    }

    // ==================== BuildSearchBody — Sort & Other Fields ====================

    [Fact]
    public void BuildSearchBody_AlwaysIncludesSizeSortAndHighlight()
    {
        var body = OpenSearchIndexService.BuildSearchBody(
            "test", phrase: false, filter: null, pageSize: 25, pageToken: null);

        body.Should().ContainKey("size");
        body["size"].Should().Be(25);
        body.Should().ContainKey("sort");
        body.Should().ContainKey("highlight");
    }

    [Fact]
    public void BuildSearchBody_SortUsesBlockIdNotLegacyFields()
    {
        // sort must use block_id (new pipeline), not document_id/segment_type (legacy)
        var body = OpenSearchIndexService.BuildSearchBody(
            "test", phrase: false, filter: null, pageSize: 10, pageToken: null);

        var json = JsonSerializer.Serialize(body);
        json.Should().Contain("\"block_id\"");
        json.Should().NotContain("\"document_id\"");
        json.Should().NotContain("\"segment_type\"");
    }

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

        var (results, totalCount, _) = OpenSearchIndexService.ParseSearchResponse(
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

        var (results, _, _) = OpenSearchIndexService.ParseSearchResponse(
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

        var (results, _, _) = OpenSearchIndexService.ParseSearchResponse(
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

        var (_, _, nextToken) = OpenSearchIndexService.ParseSearchResponse(
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

        var (_, _, nextToken) = OpenSearchIndexService.ParseSearchResponse(
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

        var (results, totalCount, nextToken) = OpenSearchIndexService.ParseSearchResponse(
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

        var (results, _, _) = OpenSearchIndexService.ParseSearchResponse(
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

        var (results, _, _) = OpenSearchIndexService.ParseSearchResponse(
            responseJson, phrase: false, pageSize: 10);

        results.Should().HaveCount(1);
        results[0].Score.Should().Be(0);
    }

    [Fact]
    public void ParseSearchResponse_WithNoHitsProperty_ReturnsEmptyResults()
    {
        // Response without "hits" at root level — tests the hasHits guard
        var responseJson = @"{ ""error"": ""something"" }";

        var (results, totalCount, nextToken) = OpenSearchIndexService.ParseSearchResponse(
            responseJson, phrase: false, pageSize: 10);

        results.Should().BeEmpty();
        totalCount.Should().Be(0);
        nextToken.Should().BeNull();
    }

    // ==================== BuildIndexBody — Block Pipeline Fields ====================

    [Fact]
    public void BuildIndexBody_IncludesBlockPipelineFields()
    {
        var body = OpenSearchIndexService.BuildIndexBody();
        var json = JsonSerializer.Serialize(body);

        // MinerU blocks pipeline fields must be present in mapping
        json.Should().Contain("\"parse_id\"");
        json.Should().Contain("\"document_file_id\"");
        json.Should().Contain("\"file_name\"");
        json.Should().Contain("\"block_id\"");
        json.Should().Contain("\"block_type\"");
        json.Should().Contain("\"sort_index\"");
        json.Should().Contain("\"image_id\"");

        // Each field must have correct type
        json.Should().Contain("\"block_id\":{\"type\":\"keyword\"}");
        json.Should().Contain("\"parse_id\":{\"type\":\"keyword\"}");
        json.Should().Contain("\"document_file_id\":{\"type\":\"keyword\"}");
        json.Should().Contain("\"file_name\":{\"type\":\"keyword\"}");
        json.Should().Contain("\"block_type\":{\"type\":\"keyword\"}");
        json.Should().Contain("\"image_id\":{\"type\":\"keyword\"}");
        json.Should().Contain("\"sort_index\":{\"type\":\"integer\"}");
    }

    [Fact]
    public void BuildIndexBody_DoesNotContainLegacyFields()
    {
        // Legacy IngestionWorker pipeline removed — its fields must not appear in mapping
        var body = OpenSearchIndexService.BuildIndexBody();
        var json = JsonSerializer.Serialize(body);

        json.Should().NotContain("\"document_id\"");
        json.Should().NotContain("\"document_title\"");
        json.Should().NotContain("\"sentence_id\"");
        json.Should().NotContain("\"question_id\"");
        json.Should().NotContain("\"segment_type\"");
        json.Should().NotContain("\"start_offset\"");
        json.Should().NotContain("\"end_offset\"");
    }

    [Fact]
    public void BuildIndexBody_RetainsSharedFieldsAndAnalyzers()
    {
        var body = OpenSearchIndexService.BuildIndexBody();
        var json = JsonSerializer.Serialize(body);

        // Shared fields (metadata + page + text + timestamp)
        json.Should().Contain("\"subject\"");
        json.Should().Contain("\"grade\"");
        json.Should().Contain("\"year\"");
        json.Should().Contain("\"page_number\"");
        json.Should().Contain("\"created_at\"");

        // Text field with custom analyzer + exact subfield
        json.Should().Contain("\"english_custom\"");
        json.Should().Contain("\"english_stemmer\"");
        json.Should().Contain("\"english_stop\"");
        json.Should().Contain("\"english_phrase\"");
        json.Should().Contain("\"exact\"");
    }
}
