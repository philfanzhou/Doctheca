using System.Text.Json;
using FluentAssertions;
using global::Doctheca.Service;
using Xunit;

namespace Doctheca.Tests;

public class OpenSearchIndexServiceTests
{
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

    // ==================== [Gen-2] BuildIndexBody — minerU mapping fields ====================

    [Fact]
    public void BuildIndexBody_IncludesMinerUFields()
    {
        var body = OpenSearchIndexService.BuildIndexBody();
        var json = JsonSerializer.Serialize(body);

        // minerU bbox components
        json.Should().Contain("\"x0\":{\"type\":\"float\"}");
        json.Should().Contain("\"y0\":{\"type\":\"float\"}");
        json.Should().Contain("\"x1\":{\"type\":\"float\"}");
        json.Should().Contain("\"y1\":{\"type\":\"float\"}");
        // minerU score
        json.Should().Contain("\"score\":{\"type\":\"float\"}");
        // has_image boolean
        json.Should().Contain("\"has_image\":{\"type\":\"boolean\"}");
        // sub_type keyword
        json.Should().Contain("\"sub_type\":{\"type\":\"keyword\"}");
        // text_level integer
        json.Should().Contain("\"text_level\":{\"type\":\"integer\"}");
        // text_format keyword
        json.Should().Contain("\"text_format\":{\"type\":\"keyword\"}");
        // caption text with english_custom analyzer
        json.Should().Contain("\"caption\"");
        json.Should().Contain("\"english_custom\"");
        // _meta object with block_data enabled:false
        json.Should().Contain("\"_meta\"");
        json.Should().Contain("\"enabled\":false");
        json.Should().Contain("\"block_data\"");
    }
}
