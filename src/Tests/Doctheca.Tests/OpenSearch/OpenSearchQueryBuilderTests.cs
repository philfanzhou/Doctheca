using System;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Doctheca.Domain.Models;
using Doctheca.Service.OpenSearch;
using Xunit;

namespace Doctheca.Tests.OpenSearch;

public class OpenSearchQueryBuilderTests
{
    // ==================== BuildSearchBody — Query Construction ====================

    [Fact]
    public void BuildSearchBody_WithPhraseQuery_UsesMatchPhraseOnTextExact()
    {
        var body = OpenSearchQueryBuilder.BuildSearchBody(
            "machine learning", phrase: true, filter: null, pageSize: 10, pageToken: null);

        var json = JsonSerializer.Serialize(body);
        json.Should().Contain("match_phrase");
        json.Should().Contain("text.exact");
        json.Should().NotContain("\"match\":");
    }

    [Fact]
    public void BuildSearchBody_WithStemmedQuery_UsesMatchOnText()
    {
        var body = OpenSearchQueryBuilder.BuildSearchBody(
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

        var body = OpenSearchQueryBuilder.BuildSearchBody(
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
        var body = OpenSearchQueryBuilder.BuildSearchBody(
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

        var body = OpenSearchQueryBuilder.BuildSearchBody(
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

        var body = OpenSearchQueryBuilder.BuildSearchBody(
            "test", phrase: false, filter: null, pageSize: 10, pageToken);

        body.Should().ContainKey("search_after");
    }

    [Fact]
    public void BuildSearchBody_WithInvalidPageToken_DoesNotIncludeSearchAfter()
    {
        var body = OpenSearchQueryBuilder.BuildSearchBody(
            "test", phrase: false, filter: null, pageSize: 10, pageToken: "!!!invalid base64!!!");

        body.Should().NotContainKey("search_after");
    }

    [Fact]
    public void BuildSearchBody_WithNullOrEmptyPageToken_DoesNotIncludeSearchAfter()
    {
        var body1 = OpenSearchQueryBuilder.BuildSearchBody(
            "test", phrase: false, filter: null, pageSize: 10, pageToken: null);
        body1.Should().NotContainKey("search_after");

        var body2 = OpenSearchQueryBuilder.BuildSearchBody(
            "test", phrase: false, filter: null, pageSize: 10, pageToken: "");
        body2.Should().NotContainKey("search_after");
    }

    // ==================== BuildSearchBody — Sort & Other Fields ====================

    [Fact]
    public void BuildSearchBody_AlwaysIncludesSizeSortAndHighlight()
    {
        var body = OpenSearchQueryBuilder.BuildSearchBody(
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
        var body = OpenSearchQueryBuilder.BuildSearchBody(
            "test", phrase: false, filter: null, pageSize: 10, pageToken: null);

        var json = JsonSerializer.Serialize(body);
        json.Should().Contain("\"block_id\"");
        json.Should().NotContain("\"document_id\"");
        json.Should().NotContain("\"segment_type\"");
    }

    // ==================== [Gen-2] BuildSearchBody — minerU filter clauses ====================

    [Fact]
    public void BuildSearchBody_WithBlockTypeFilter_IncludesTerm()
    {
        var filter = new SearchFilterModel { BlockType = "image" };

        var body = OpenSearchQueryBuilder.BuildSearchBody(
            "test", phrase: false, filter, pageSize: 10, pageToken: null);

        var json = JsonSerializer.Serialize(body);
        json.Should().Contain("\"bool\"");
        json.Should().Contain("\"filter\"");
        json.Should().Contain("\"block_type\"");
        json.Should().Contain("\"image\"");
    }

    [Fact]
    public void BuildSearchBody_WithPageNumberAndHasImage_WrapsAllInFilter()
    {
        var filter = new SearchFilterModel { PageNumber = 3, HasImage = true };

        var body = OpenSearchQueryBuilder.BuildSearchBody(
            "test", phrase: false, filter, pageSize: 10, pageToken: null);

        var json = JsonSerializer.Serialize(body);
        json.Should().Contain("\"bool\"");
        json.Should().Contain("\"filter\"");
        json.Should().Contain("\"page_number\"");
        json.Should().Contain("\"has_image\"");
        json.Should().Contain("true"); // HasImage=true
    }

    [Fact]
    public void BuildSearchBody_WithNoMinerUFilter_OutputEqualsV1()
    {
        // AC-17 zero regression: when all minerU filters are null, output must not contain minerU filter clauses
        var filter = new SearchFilterModel
        {
            Subject = "英语",  // V1 filter only
            // All minerU fields null/default
        };

        var body = OpenSearchQueryBuilder.BuildSearchBody(
            "test", phrase: false, filter, pageSize: 10, pageToken: null);

        var json = JsonSerializer.Serialize(body);
        // V1 filter should be present
        json.Should().Contain("\"subject\"");
        // minerU filter clauses must NOT be present (block_type as filter, sub_type, text_level, etc.)
        json.Should().NotContain("\"block_type\"");
        json.Should().NotContain("\"sub_type\"");
        json.Should().NotContain("\"text_level\"");
        json.Should().NotContain("\"text_format\"");
        json.Should().NotContain("\"has_image\"");
        // page_number appears in mapping but NOT as a filter term when PageNumber is null
        // parse_id / document_file_id should not appear as filter terms
    }

    [Fact]
    public void BuildSearchBody_WithKeywordAndMinerUFilter_SplitsMustAndFilter()
    {
        var filter = new SearchFilterModel
        {
            BlockType = "text",
            BlockSubType = "image_caption",
            ParseId = Guid.Parse("12345678-1234-1234-1234-123456789012")
        };

        var body = OpenSearchQueryBuilder.BuildSearchBody(
            "keyword", phrase: false, filter, pageSize: 10, pageToken: null);

        var json = JsonSerializer.Serialize(body);
        // keyword goes to must
        json.Should().Contain("\"must\"");
        // minerU exact conditions go to filter
        json.Should().Contain("\"filter\"");
        json.Should().Contain("\"block_type\"");
        json.Should().Contain("\"sub_type\"");
        json.Should().Contain("\"parse_id\"");
    }

    [Fact]
    public void BuildSearchBody_WithAllMinerUFilters_IncludesAllTerms()
    {
        var filter = new SearchFilterModel
        {
            BlockType = "image",
            BlockSubType = "image_body",
            PageNumber = 5,
            TextLevel = 0,
            TextFormat = "latex",
            ParseId = Guid.NewGuid(),
            DocumentFileId = Guid.NewGuid(),
            HasImage = true
        };

        var body = OpenSearchQueryBuilder.BuildSearchBody(
            "test", phrase: false, filter, pageSize: 10, pageToken: null);

        var json = JsonSerializer.Serialize(body);
        json.Should().Contain("\"block_type\"");
        json.Should().Contain("\"sub_type\"");
        json.Should().Contain("\"page_number\"");
        json.Should().Contain("\"text_level\"");
        json.Should().Contain("\"text_format\"");
        json.Should().Contain("\"parse_id\"");
        json.Should().Contain("\"document_file_id\"");
        json.Should().Contain("\"has_image\"");
    }
}
