using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;

namespace Ruoyu.Study.DocLibrary.Domain.Services;

public interface IDocumentParseBlockService
{
    /// <summary>
    /// Parse content_list.json (MinerU output) and bulk insert blocks.
    /// Existing blocks for the parse are deleted first (overwrite strategy).
    /// </summary>
    Task InsertBlocksFromContentListAsync(Guid parseId, string contentListJson, IDictionary<string, Guid>? imageNameToId = null);
}

public class DocumentParseBlockService : IDocumentParseBlockService
{
    private readonly IDocumentParseBlockRepository _repository;
    private readonly ILogger<DocumentParseBlockService>? _logger;

    public DocumentParseBlockService(IDocumentParseBlockRepository repository)
    {
        _repository = repository;
    }

    public DocumentParseBlockService(IDocumentParseBlockRepository repository, ILogger<DocumentParseBlockService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task InsertBlocksFromContentListAsync(Guid parseId, string contentListJson, IDictionary<string, Guid>? imageNameToId = null)
    {
        if (string.IsNullOrWhiteSpace(contentListJson))
        {
            _logger?.LogWarning("Empty contentListJson for parse {ParseId}", parseId);
            return;
        }

        // Snapshot raw block JSONs (strings) before JsonDocument is disposed
        // — JsonElement references its parent JsonDocument and becomes invalid after disposal.
        List<string> rawBlocks;
        try
        {
            using var doc = JsonDocument.Parse(contentListJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                _logger?.LogWarning("content_list.json root is not an array for parse {ParseId}", parseId);
                return;
            }
            rawBlocks = doc.RootElement.EnumerateArray()
                .Select(el => el.GetRawText())
                .ToList();
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "Failed to parse content_list.json for parse {ParseId}", parseId);
            return;
        }

        _logger?.LogInformation("Parsing {BlockCount} blocks for parse {ParseId}", rawBlocks.Count, parseId);

        var blocks = new List<DocumentParseBlockModel>(rawBlocks.Count);
        var perPageCounter = new Dictionary<int, int>();

        for (int i = 0; i < rawBlocks.Count; i++)
        {
            var model = ParseBlock(rawBlocks[i], parseId, imageNameToId, perPageCounter, i);
            if (model != null) blocks.Add(model);
        }

        if (blocks.Count > 0)
        {
            await _repository.AddBlocksAsync(parseId, blocks);
            _logger?.LogInformation("Inserted {BlockCount} blocks for parse {ParseId}", blocks.Count, parseId);
        }
    }

    private static DocumentParseBlockModel? ParseBlock(
        string rawBlockJson,
        Guid parseId,
        IDictionary<string, Guid>? imageNameToId,
        Dictionary<int, int> perPageCounter,
        int globalIndex)
    {
        using var doc = JsonDocument.Parse(rawBlockJson);
        var el = doc.RootElement;

        // Required: type
        if (!el.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var blockType = typeEl.GetString();
        if (string.IsNullOrEmpty(blockType)) return null;

        // Page ID (default 0)
        var pageId = 0;
        if (el.TryGetProperty("page_id", out var pageIdEl) && pageIdEl.ValueKind == JsonValueKind.Number)
        {
            pageId = pageIdEl.GetInt32();
        }

        // Sort index per page (preserve reading order)
        perPageCounter.TryGetValue(pageId, out var currentCount);
        perPageCounter[pageId] = currentCount + 1;
        var sortIndex = currentCount;

        // Text content (different fields by type)
        string? textContent = ExtractTextContent(el);

        // Image reference
        Guid? imageId = null;
        if (blockType == "image" && imageNameToId != null)
        {
            // MinerU uses "img_path" (relative to ZIP) for image blocks
            var imgPath = el.TryGetProperty("img_path", out var imgPathEl) ? imgPathEl.GetString() : null;
            if (imgPath != null)
            {
                // imgPath is "images/xxx.jpg", extract filename
                var imgName = System.IO.Path.GetFileName(imgPath);
                if (imageNameToId.TryGetValue(imgName, out var id))
                {
                    imageId = id;
                }
            }
        }

        return new DocumentParseBlockModel
        {
            Id = Guid.NewGuid(),
            ParseId = parseId,
            PageId = pageId,
            SortIndex = sortIndex,
            BlockType = blockType,
            TextContent = textContent,
            ImageId = imageId,
            BlockData = rawBlockJson,
            CreatedAt = DateTimeOffset.UtcNow,
            // [Gen-2] minerU structured fields extracted from block JSON
            SubType = ExtractSubType(el),
            TextLevel = ExtractTextLevel(el),
            TextFormat = ExtractTextFormat(el),
            BboxX0 = ExtractBboxComponent(el, 0),
            BboxY0 = ExtractBboxComponent(el, 1),
            BboxX1 = ExtractBboxComponent(el, 2),
            BboxY1 = ExtractBboxComponent(el, 3),
            MineruScore = ExtractScore(el),
            Caption = ExtractCaption(el),
        };
    }

    private static string? ExtractTextContent(JsonElement el)
    {
        // Common fields: text / content / body (table HTML)
        foreach (var propName in new[] { "text", "content", "body" })
        {
            if (el.TryGetProperty(propName, out var prop))
            {
                return prop.ValueKind switch
                {
                    JsonValueKind.String => prop.GetString(),
                    _ => prop.GetRawText(),
                };
            }
        }
        return null;
    }

    // [Gen-2] minerU field extraction helpers

    private static string? ExtractSubType(JsonElement el)
    {
        // minerU official field: sub_type at top level (e.g. image_caption, table_body, code, algorithm, text, ref_text, seal)
        if (el.TryGetProperty("sub_type", out var stEl) && stEl.ValueKind == JsonValueKind.String)
        {
            var v = stEl.GetString();
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return null;
    }

    private static int ExtractTextLevel(JsonElement el)
    {
        // minerU text_level: 0=body, 1=h1, 2=h2...; absent on non-heading text → -1
        if (el.TryGetProperty("text_level", out var tlEl) && tlEl.ValueKind == JsonValueKind.Number)
        {
            return tlEl.GetInt32();
        }
        return -1;
    }

    private static string ExtractTextFormat(JsonElement el)
    {
        // minerU VLM-only field: latex / markdown / none; pipeline backend lacks this → empty string
        if (el.TryGetProperty("text_format", out var tfEl) && tfEl.ValueKind == JsonValueKind.String)
        {
            return tfEl.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    /// <summary>
    /// Extracts a single bbox component (0=x0, 1=y0, 2=x1, 3=y1) from minerU bbox array.
    /// Normalizes VLM 0-1 percentage coordinates to pipeline 0-1000 convention.
    /// </summary>
    private static float? ExtractBboxComponent(JsonElement el, int index)
    {
        if (!el.TryGetProperty("bbox", out var bboxEl) || bboxEl.ValueKind != JsonValueKind.Array)
            return null;

        var arr = bboxEl.EnumerateArray();
        var i = 0;
        foreach (var item in arr)
        {
            if (i == index)
            {
                if (item.ValueKind != JsonValueKind.Number)
                    return null;
                var value = item.GetSingle();
                // Normalize VLM 0-1 percentage to pipeline 0-1000 convention.
                // Heuristic: if max bbox value <= 1.0, treat as VLM percentage and multiply by 1000.
                // Pipeline bbox values are in [0,1000] so values > 1.0 are left as-is.
                if (value >= 0 && value <= 1.0f && AnyBboxValueExceedsOne(bboxEl) == false)
                {
                    return value * 1000f;
                }
                return value;
            }
            i++;
        }
        return null;
    }

    /// <summary>
    /// Checks whether any value in the bbox array exceeds 1.0 (indicating pipeline 0-1000 coordinate system).
    /// </summary>
    private static bool AnyBboxValueExceedsOne(JsonElement bboxEl)
    {
        foreach (var item in bboxEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.GetSingle() > 1.0f)
                return true;
        }
        return false;
    }

    private static double? ExtractScore(JsonElement el)
    {
        // minerU confidence score (VLM backend); pipeline backend typically lacks this field
        if (el.TryGetProperty("score", out var scEl) && scEl.ValueKind == JsonValueKind.Number)
        {
            return scEl.GetDouble();
        }
        return null;
    }

    /// <summary>
    /// Derives a searchable caption string by concatenating image_caption / table_caption /
    /// chart_caption / code_caption array texts. Each caption array element may be a string
    /// or an object with text/content/body. Returns null when no caption fields are present.
    /// </summary>
    private static string? ExtractCaption(JsonElement el)
    {
        var captionProps = new[] { "image_caption", "table_caption", "chart_caption", "code_caption" };
        var parts = new List<string>();
        foreach (var propName in captionProps)
        {
            if (el.TryGetProperty(propName, out var capEl) && capEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in capEl.EnumerateArray())
                {
                    var text = item.ValueKind switch
                    {
                        JsonValueKind.String => item.GetString(),
                        JsonValueKind.Object when item.TryGetProperty("text", out var tEl) => tEl.GetString(),
                        JsonValueKind.Object when item.TryGetProperty("content", out var cEl) => cEl.GetString(),
                        JsonValueKind.Object when item.TryGetProperty("body", out var bEl) => bEl.GetString(),
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add(text);
                }
            }
        }
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }
}
