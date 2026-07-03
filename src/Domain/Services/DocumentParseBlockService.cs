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
}
