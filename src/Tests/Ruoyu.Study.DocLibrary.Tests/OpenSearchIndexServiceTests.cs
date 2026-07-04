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
        json.Should().Contain("\"document_title\"");
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
        json.Should().NotContain("\"document_title\"");
    }

    // ==================== BuildSearchBody — Page Token ====================

    [Fact]
    public void BuildSearchBody_WithValidPageToken_IncludesSearchAfter()
    {
        var sortArray = "[1.5, \"doc1\", \"sentence\"]";
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

    // ==================== BuildSearchBody — Other Fields ====================

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

    // ==================== ParseSearchResponse — Normal Cases ====================

    [Fact]
    public void ParseSearchResponse_WithValidJson_ReturnsResultsWithCorrectSegmentIds()
    {
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

        var (results, totalCount, _) = OpenSearchIndexService.ParseSearchResponse(
            responseJson, phrase: false, pageSize: 10);

        totalCount.Should().Be(2);
        results.Should().HaveCount(2);

        results[0].DocumentName.Should().Be("test.pdf");
        results[0].PageNumber.Should().Be(1);
        results[0].AssociatedText.Should().Be("Hello world");
        results[0].Score.Should().Be(1.5);
        results[0].MatchType.Should().Be(SearchMatchType.Stemmed);
        results[0].SegmentId.Should().Be("s1");

        results[1].DocumentName.Should().Be("test.pdf");
        results[1].PageNumber.Should().Be(2);
        results[1].AssociatedText.Should().Be("What is the capital?");
        results[1].MatchType.Should().Be(SearchMatchType.Stemmed);
        results[1].SegmentId.Should().Be("q1");
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

        var (_, _, nextToken) = OpenSearchIndexService.ParseSearchResponse(
            responseJson, phrase: false, pageSize: 1);

        nextToken.Should().NotBeNull();
        // Verify it's valid Base64 that decodes to the sort array
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(nextToken!));
        decoded.Should().Contain("1");
        decoded.Should().Contain("doc1");
        decoded.Should().Contain("sentence");
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
                        ""sort"": [1.0, ""doc1"", ""sentence""]
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
                            ""document_title"": ""test.pdf"",
                            ""page_number"": 1,
                            ""text"": ""Hello"",
                            ""segment_type"": ""sentence"",
                            ""sentence_id"": ""s1""
                        },
                        ""sort"": [1.0, ""doc1"", ""sentence""]
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

    // ==================== ParseSearchResponse — New Block Fields (UT-OBI-08) ====================

    [Fact]
    public void ParseSearchResponse_WithBlockFields_PrefersBlockIdAndFileName()
    {
        // New MinerU blocks pipeline data: file_name + block_id should take precedence
        // over legacy document_title + sentence_id/question_id
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 1 },
                ""hits"": [
                    {
                        ""_score"": 1.5,
                        ""_source"": {
                            ""file_name"": ""lecture.pdf"",
                            ""page_number"": 5,
                            ""text"": ""Newton's first law"",
                            ""block_id"": ""blk-abc-123"",
                            ""block_type"": ""text"",
                            ""sort_index"": 7,
                            ""image_id"": ""img-001"",
                            ""parse_id"": ""parse-xyz"",
                            ""document_file_id"": ""file-001"",
                            ""document_title"": ""legacy.pdf"",
                            ""sentence_id"": ""s-legacy"",
                            ""question_id"": ""q-legacy"",
                            ""segment_type"": ""sentence"",
                            ""start_offset"": 100,
                            ""end_offset"": 200
                        },
                        ""sort"": [1.5, ""file-001"", ""block""]
                    }
                ]
            }
        }";

        var (results, _, _) = OpenSearchIndexService.ParseSearchResponse(
            responseJson, phrase: false, pageSize: 10);

        results.Should().HaveCount(1);
        // segment_id prefers block_id over sentence_id/question_id
        results[0].SegmentId.Should().Be("blk-abc-123");
        // document_name prefers file_name over document_title
        results[0].DocumentName.Should().Be("lecture.pdf");
        results[0].PageNumber.Should().Be(5);
        results[0].AssociatedText.Should().Be("Newton's first law");
        results[0].Score.Should().Be(1.5);
        results[0].MatchType.Should().Be(SearchMatchType.Stemmed);
    }

    [Fact]
    public void ParseSearchResponse_WithBlockFields_PhraseSetsExactPhraseMatchType()
    {
        // Phrase query on new block data — matchType must be ExactPhrase
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 1 },
                ""hits"": [
                    {
                        ""_score"": 2.5,
                        ""_source"": {
                            ""file_name"": ""exam.pdf"",
                            ""page_number"": 1,
                            ""text"": ""Hello world"",
                            ""block_id"": ""blk-001"",
                            ""block_type"": ""text""
                        },
                        ""sort"": [2.5, ""file-001"", ""block""]
                    }
                ]
            }
        }";

        var (results, _, _) = OpenSearchIndexService.ParseSearchResponse(
            responseJson, phrase: true, pageSize: 10);

        results.Should().HaveCount(1);
        results[0].MatchType.Should().Be(SearchMatchType.ExactPhrase);
        results[0].SegmentId.Should().Be("blk-001");
        results[0].DocumentName.Should().Be("exam.pdf");
    }

    // ==================== ParseSearchResponse — Field Fallback (UT-OBI-09) ====================

    [Fact]
    public void ParseSearchResponse_WithOnlyLegacyFields_FallsBackToLegacyIds()
    {
        // Pure legacy LLM pipeline data — no block_id/file_name, must fall back
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 1 },
                ""hits"": [
                    {
                        ""_score"": 0.9,
                        ""_source"": {
                            ""document_title"": ""legacy-doc.pdf"",
                            ""page_number"": 3,
                            ""text"": ""photosynthesis"",
                            ""segment_type"": ""question"",
                            ""question_id"": ""q-legacy-001"",
                            ""start_offset"": 0,
                            ""end_offset"": 14
                        },
                        ""sort"": [0.9, ""doc-legacy"", ""question""]
                    }
                ]
            }
        }";

        var (results, _, _) = OpenSearchIndexService.ParseSearchResponse(
            responseJson, phrase: false, pageSize: 10);

        results.Should().HaveCount(1);
        // segment_id falls back to question_id (segment_type == question)
        results[0].SegmentId.Should().Be("q-legacy-001");
        // document_name falls back to document_title
        results[0].DocumentName.Should().Be("legacy-doc.pdf");
        results[0].PageNumber.Should().Be(3);
        results[0].StartOffset.Should().Be(0);
        results[0].EndOffset.Should().Be(14);
    }

    [Fact]
    public void ParseSearchResponse_WithEmptyBlockId_FallsBackToLegacySentenceId()
    {
        // block_id present but empty — must fall back to sentence_id
        var responseJson = @"{
            ""hits"": {
                ""total"": { ""value"": 1 },
                ""hits"": [
                    {
                        ""_score"": 1.0,
                        ""_source"": {
                            ""file_name"": """",
                            ""document_title"": ""fallback.pdf"",
                            ""page_number"": 1,
                            ""text"": ""text content"",
                            ""block_id"": """",
                            ""segment_type"": ""sentence"",
                            ""sentence_id"": ""s-fallback""
                        },
                        ""sort"": [1.0, ""doc-1"", ""sentence""]
                    }
                ]
            }
        }";

        var (results, _, _) = OpenSearchIndexService.ParseSearchResponse(
            responseJson, phrase: false, pageSize: 10);

        results.Should().HaveCount(1);
        // block_id is empty, falls back to sentence_id
        results[0].SegmentId.Should().Be("s-fallback");
        // file_name is empty, falls back to document_title
        results[0].DocumentName.Should().Be("fallback.pdf");
    }

    // ==================== BuildIndexBody — New Fields (UT-OBI-10) ====================

    [Fact]
    public void BuildIndexBody_IncludesNewBlockPipelineFields()
    {
        var body = OpenSearchIndexService.BuildIndexBody();
        var json = JsonSerializer.Serialize(body);

        // New MinerU blocks pipeline fields must be present in mapping
        json.Should().Contain("\"parse_id\"");
        json.Should().Contain("\"document_file_id\"");
        json.Should().Contain("\"file_name\"");
        json.Should().Contain("\"block_id\"");
        json.Should().Contain("\"block_type\"");
        json.Should().Contain("\"sort_index\"");
        json.Should().Contain("\"image_id\"");

        // Each new field must be mapped as keyword (block_id/file_name/parse_id/etc.)
        json.Should().Contain("\"block_id\":{\"type\":\"keyword\"}");
        json.Should().Contain("\"parse_id\":{\"type\":\"keyword\"}");
        json.Should().Contain("\"document_file_id\":{\"type\":\"keyword\"}");
        json.Should().Contain("\"file_name\":{\"type\":\"keyword\"}");
        json.Should().Contain("\"block_type\":{\"type\":\"keyword\"}");
        json.Should().Contain("\"image_id\":{\"type\":\"keyword\"}");
        // sort_index is integer
        json.Should().Contain("\"sort_index\":{\"type\":\"integer\"}");
    }

    [Fact]
    public void BuildIndexBody_RetainsLegacyLLMPipelineFields()
    {
        var body = OpenSearchIndexService.BuildIndexBody();
        var json = JsonSerializer.Serialize(body);

        // Legacy LLM segmentation fields must be retained for backward compatibility
        json.Should().Contain("\"document_id\"");
        json.Should().Contain("\"document_title\"");
        json.Should().Contain("\"sentence_id\"");
        json.Should().Contain("\"question_id\"");
        json.Should().Contain("\"segment_type\"");
        json.Should().Contain("\"start_offset\"");
        json.Should().Contain("\"end_offset\"");
    }

    [Fact]
    public void BuildIndexBody_RetainsSharedFieldsAndAnalyzers()
    {
        var body = OpenSearchIndexService.BuildIndexBody();
        var json = JsonSerializer.Serialize(body);

        // Shared fields (both pipelines)
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
