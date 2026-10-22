using System.Text.Json;
using Doctheca.Service.OpenSearch;
using FluentAssertions;
using Xunit;

namespace Doctheca.Tests.OpenSearch;

public sealed class AnnotationSnippetTests
{
    [Theory]
    [InlineData("{\"text\":[\"body <em>hit</em>\"],\"caption\":[\"caption <em>hit</em>\"],\"footnote\":[\"note <em>hit</em>\"]}", "body <em>hit</em>")]
    [InlineData("{\"caption\":[\"caption <em>hit</em>\"],\"footnote\":[\"note <em>hit</em>\"]}", "caption <em>hit</em>")]
    [InlineData("{\"text\":[],\"caption\":[],\"footnote\":[\"note <em>hit</em>\"]}", "note <em>hit</em>")]
    [InlineData("{}", "raw body")]
    public void MatchedHighlightWinsBeforeRawBody_WithDeterministicFieldPriority(string highlights, string expected)
    {
        using var highlight = JsonDocument.Parse(highlights);
        var json = JsonSerializer.Serialize(new
        {
            hits = new
            {
                total = new { value = 1 },
                hits = new[] { new { _source = new { text = "raw body", caption = "raw caption", footnote = "raw note" }, highlight = highlight.RootElement } }
            }
        });
        var result = OpenSearchResponseParser.ParseSearchResponse(json, true, 100).Results.Single();
        result.AssociatedText.Should().Be(expected);
        result.Caption.Should().Be("raw caption");
        result.Footnote.Should().Be("raw note");
        result.StartOffset.Should().Be(0);
        result.EndOffset.Should().Be(0);
    }

    [Theory]
    [InlineData(null, "  图题\nα  ", "note", "  图题\nα  ")]
    [InlineData(" \t", "", "  注释\nβ  ", "  注释\nβ  ")]
    [InlineData(null, null, null, "")]
    public void NoHighlight_UsesFirstNonWhitespaceRawField_WithExistingEmptyCaptionConvention(
        string? text, string? caption, string? footnote, string expected)
    {
        var json = JsonSerializer.Serialize(new
        {
            hits = new { total = new { value = 1 }, hits = new[] { new { _source = new { text, caption, footnote } } } }
        });
        var result = OpenSearchResponseParser.ParseSearchResponse(json, false, 100).Results.Single();
        result.AssociatedText.Should().Be(expected);
        result.Caption.Should().Be(string.IsNullOrEmpty(caption) ? null : caption);
        result.Footnote.Should().Be(string.IsNullOrEmpty(footnote) ? null : footnote);
    }
}
