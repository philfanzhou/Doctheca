using System.Text;
using System.Text.Json;
using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Service.OpenSearch;

internal static class OpenSearchResponseParser
{
    /// <summary>
    /// Parses the OpenSearch search response JSON (pure logic, testable).
    /// </summary>
    internal static (List<SearchResultModel> Results, int TotalCount, string? NextToken) ParseSearchResponse(
        string responseJson, bool phrase, int pageSize)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var hasHits = root.TryGetProperty("hits", out var hitsEl);
        var totalCount = hasHits
            && hitsEl.TryGetProperty("total", out var totalEl)
            && totalEl.TryGetProperty("value", out var valueEl)
            ? valueEl.GetInt32()
            : 0;

        var results = new List<SearchResultModel>();
        JsonElement lastSort = default;
        var hasLastSort = false;

        if (hasHits && hitsEl.TryGetProperty("hits", out var hitArray))
        {
            foreach (var hit in hitArray.EnumerateArray())
            {
                var source = hit.GetProperty("_source");

                var segmentId = source.TryGetProperty("block_id", out var bidEl) ? bidEl.GetString() ?? "" : "";

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

                var documentName = source.TryGetProperty("file_name", out var fnEl) ? fnEl.GetString() ?? "" : "";

                // [Gen-2] minerU block-level fields from _source (null/defaults when absent — robust)
                string? blockData = null;
                if (source.TryGetProperty("_meta", out var metaEl)
                    && metaEl.ValueKind == JsonValueKind.Object
                    && metaEl.TryGetProperty("block_data", out var bdEl)
                    && bdEl.ValueKind == JsonValueKind.String)
                {
                    blockData = bdEl.GetString();
                }

                float[]? bbox = null;
                var x0 = OpenSearchJsonHelper.TryGetFloat(source, "x0");
                var y0 = OpenSearchJsonHelper.TryGetFloat(source, "y0");
                var x1 = OpenSearchJsonHelper.TryGetFloat(source, "x1");
                var y1 = OpenSearchJsonHelper.TryGetFloat(source, "y1");
                if (x0.HasValue || y0.HasValue || x1.HasValue || y1.HasValue)
                {
                    bbox = new float[4];
                    bbox[0] = x0 ?? 0f;
                    bbox[1] = y0 ?? 0f;
                    bbox[2] = x1 ?? 0f;
                    bbox[3] = y1 ?? 0f;
                }

                var mineruScore = OpenSearchJsonHelper.TryGetDouble(source, "score");
                var subType = OpenSearchJsonHelper.TryGetString(source, "sub_type");
                var textLevel = OpenSearchJsonHelper.TryGetInt(source, "text_level");
                var textFormat = OpenSearchJsonHelper.TryGetString(source, "text_format");
                var caption = OpenSearchJsonHelper.TryGetString(source, "caption");

                results.Add(new SearchResultModel
                {
                    DocumentName = documentName,
                    PageNumber = source.TryGetProperty("page_number", out var pnEl) ? pnEl.GetInt32() : 0,
                    AssociatedText = associatedText,
                    Score = score,
                    MatchType = phrase ? SearchMatchType.ExactPhrase : SearchMatchType.Stemmed,
                    SegmentId = segmentId,
                    StartOffset = 0,
                    EndOffset = 0,
                    CreatedAt = source.TryGetProperty("created_at", out var caEl) && DateTimeOffset.TryParse(caEl.GetString(), out var ca) ? ca : null,
                    // [Gen-2] minerU fields
                    BlockData = blockData,
                    Bbox = bbox,
                    MineruScore = mineruScore,
                    SubType = subType,
                    TextLevel = textLevel,
                    TextFormat = textFormat,
                    Caption = caption
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
