using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Doctheca.Domain.Models;

namespace Doctheca.Service.OpenSearch;

internal static class OpenSearchQueryBuilder
{
    /// <summary>
    /// Builds the OpenSearch search request body (pure logic, testable).
    /// </summary>
    internal static Dictionary<string, object> BuildSearchBody(
        string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken, ILogger? logger = null)
    {
        // Build the main query
        // Phrase query uses text.exact field (english_phrase analyzer, lowercase only without stemming, ensures phrase integrity)
        // Non-phrase query uses text field (english_custom analyzer, stemming for expanded recall)
        // Note: OpenSearch multi-field uses "text.exact" at query time, C# anonymous objects cannot contain dots in property names, use dictionary instead
        object mainQuery;
        if (phrase)
        {
            mainQuery = new Dictionary<string, object>
            {
                ["match_phrase"] = new Dictionary<string, object> { ["text.exact"] = new { query } }
            };
        }
        else
        {
            mainQuery = new { match = new { text = new { query } } };
        }

        // Build filter clauses
        var filterClauses = new List<object>();
        if (filter != null)
        {
            // V1 filters (unchanged — zero regression)
            if (!string.IsNullOrEmpty(filter.Subject))
                filterClauses.Add(new { term = new { subject = new { value = filter.Subject } } });
            if (!string.IsNullOrEmpty(filter.Grade))
                filterClauses.Add(new { term = new { grade = new { value = filter.Grade } } });
            if (!string.IsNullOrEmpty(filter.Year))
                filterClauses.Add(new { term = new { year = new { value = filter.Year } } });
            // file_name is a keyword field; use term for exact matching
            if (!string.IsNullOrEmpty(filter.DocumentTitle))
                filterClauses.Add(new { term = new { file_name = new { value = filter.DocumentTitle } } });

            // [Gen-2] minerU block-level filters (all term+filter; null → not added → zero regression)
            if (!string.IsNullOrEmpty(filter.BlockType))
                filterClauses.Add(new { term = new { block_type = new { value = filter.BlockType } } });
            if (!string.IsNullOrEmpty(filter.BlockSubType))
                filterClauses.Add(new { term = new { sub_type = new { value = filter.BlockSubType } } });
            if (filter.PageNumber.HasValue)
                filterClauses.Add(new { term = new { page_number = new { value = filter.PageNumber.Value } } });
            if (filter.TextLevel.HasValue)
                filterClauses.Add(new { term = new { text_level = new { value = filter.TextLevel.Value } } });
            if (!string.IsNullOrEmpty(filter.TextFormat))
                filterClauses.Add(new { term = new { text_format = new { value = filter.TextFormat } } });
            if (filter.ParseId.HasValue)
                filterClauses.Add(new { term = new { parse_id = new { value = filter.ParseId.Value.ToString() } } });
            if (filter.DocumentFileId.HasValue)
                filterClauses.Add(new { term = new { document_file_id = new { value = filter.DocumentFileId.Value.ToString() } } });
            if (filter.HasImage.HasValue)
                filterClauses.Add(new { term = new { has_image = new { value = filter.HasImage.Value } } });
        }

        object queryObj = filterClauses.Count > 0
            ? new { @bool = new { must = mainQuery, filter = filterClauses } }
            : mainQuery;

        // Decode page token for search_after
        List<object>? searchAfter = null;
        if (!string.IsNullOrEmpty(pageToken))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(pageToken));
                searchAfter = JsonSerializer.Deserialize<List<object>>(decoded);
            }
            catch (Exception ex)
            {
                // pageToken is opaque client input; invalid tokens are expected occasionally
                logger?.LogDebug(ex, "Failed to decode page token, starting from first page");
                searchAfter = null;
            }
        }

        var searchBody = new Dictionary<string, object>
        {
            ["size"] = pageSize,
            ["query"] = queryObj,
            ["sort"] = new object[]
            {
                new { _score = new { order = "desc" } },
                new { block_id = new { order = "asc" } }
            },
            ["highlight"] = new
            {
                fields = new
                {
                    text = new { }
                },
                pre_tags = new[] { "<em>" },
                post_tags = new[] { "</em>" }
            }
        };

        if (searchAfter != null)
        {
            searchBody["search_after"] = searchAfter;
        }

        return searchBody;
    }
}
