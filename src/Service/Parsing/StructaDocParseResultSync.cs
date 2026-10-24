using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;
using Doctheca.Service.StructaDoc;

namespace Doctheca.Service.Parsing;

public interface IStructaDocParseResultSync
{
    /// <summary>
    /// Pull the full result of a succeeded Parse Run into local storage and mark the parse as parsed.
    /// Returns the canonical Markdown content.
    /// </summary>
    Task<string> SyncAsync(DocumentParseModel parse, Guid parseRunId, CancellationToken ct);
}

/// <summary>
/// Syncs a succeeded StructaDoc Parse Run into Doctheca local tables (ADR-0009):
/// assets become document_parse_images rows (image_path stores the StructaDoc asset ID),
/// blocks become document_parse_blocks rows, and the canonical Markdown is stored on the
/// parse record. Re-running the sync is idempotent (images and blocks are replaced).
/// </summary>
public sealed class StructaDocParseResultSync : IStructaDocParseResultSync
{
    private const int MaxTextLength = 20;
    private static readonly JsonSerializerOptions BlockDataJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStructaDocClient _client;
    private readonly IDocumentParseService _parseService;
    private readonly ILogger<StructaDocParseResultSync> _logger;

    public StructaDocParseResultSync(
        IStructaDocClient client,
        IDocumentParseService parseService,
        ILogger<StructaDocParseResultSync> logger)
    {
        _client = client;
        _parseService = parseService;
        _logger = logger;
    }

    public async Task<string> SyncAsync(DocumentParseModel parse, Guid parseRunId, CancellationToken ct)
    {
        var assets = await _client.GetAssetsAsync(parseRunId, ct);
        var blocks = await _client.GetAllBlocksAsync(parseRunId, ct);
        var markdown = await _client.GetMarkdownAsync(parseRunId, ct);
        var pages = await ReadOptionalPagesAsync(parse.Id, parseRunId, ct);
        ct.ThrowIfCancellationRequested();
        var assetIdToImageId = new Dictionary<Guid, Guid>(assets.Count);
        var images = new List<DocumentParseImageModel>();
        foreach (var asset in assets)
        {
            var image = new DocumentParseImageModel
            {
                Id = Guid.NewGuid(),
                ParseId = parse.Id,
                ImageName = asset.Name,
                // For StructaDoc-backed parses, image_path holds the asset ID (not an OSS path).
                ImagePath = asset.Id.ToString("D"),
                ContentType = string.IsNullOrWhiteSpace(asset.MediaType) ? "image/jpeg" : asset.MediaType,
            };
            images.Add(image);
            assetIdToImageId[asset.Id] = image.Id;
        }

        var blockModels = MapBlocks(parse.Id, blocks, assetIdToImageId);
        try
        {
            markdown = await _parseService.CompleteAsync(parse.Id, parseRunId, markdown, pages, images, blockModels, ct);
        }
        catch (Exception)
        {
            ct.ThrowIfCancellationRequested();
            throw new StructaDocException("Local result replacement failed; synchronization must retry.", 0, isTransient: true);
        }

        _logger.LogInformation(
            "Synced StructaDoc parse run {ParseRunId} into parse {ParseId}: {BlockCount} blocks, {AssetCount} assets",
            parseRunId, parse.Id, blockModels.Count, assets.Count);

        return markdown;
    }

    private async Task<IReadOnlyCollection<DocumentParsePageModel>> ReadOptionalPagesAsync(
        Guid parseId, Guid parseRunId, CancellationToken ct)
    {
        try
        {
            var pages = await _client.GetPagesAsync(parseRunId, ct);
            ct.ThrowIfCancellationRequested();
            if (pages is null || pages.Count == 0 || pages.Any(page => page is null || page.Number <= 0)
                || pages.Select(page => page.Number).Distinct().Count() != pages.Count)
            {
                _logger.LogWarning("Optional StructaDoc pages unavailable for parse {ParseId}: invalid or empty page set", parseId);
                return [];
            }
            return pages.Select(page => new DocumentParsePageModel(parseId, page.Number, page.Label, page.LabelSource)).ToList();
        }
        catch (Exception exception)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogWarning("Optional StructaDoc pages unavailable for parse {ParseId}: fetch failed ({FailureType})",
                parseId, exception.GetType().Name);
            return [];
        }
    }

    private static List<DocumentParseBlockModel> MapBlocks(
        Guid parseId,
        IReadOnlyList<StructaDocBlockResponse> blocks,
        IReadOnlyDictionary<Guid, Guid> assetIdToImageId)
    {
        var models = new List<DocumentParseBlockModel>(blocks.Count);
        var perPageCounter = new Dictionary<int, int>();

        foreach (var block in blocks.OrderBy(b => b.Sequence))
        {
            // StructaDoc pages are 1-based and nullable; legacy page_id is 0-based.
            var pageId = (block.PageNumber ?? 1) - 1;
            perPageCounter.TryGetValue(pageId, out var currentCount);
            perPageCounter[pageId] = currentCount + 1;

            Guid? imageId = null;
            if (block.AssetId.HasValue && assetIdToImageId.TryGetValue(block.AssetId.Value, out var mappedImageId))
            {
                imageId = mappedImageId;
            }

            models.Add(new DocumentParseBlockModel
            {
                Id = Guid.NewGuid(),
                ParseId = parseId,
                PageId = pageId,
                SortIndex = currentCount,
                // Global reading order kept as-is; sort_index stays a per-page counter.
                Sequence = block.Sequence,
                BlockType = Truncate(block.Type, MaxTextLength) ?? "unknown",
                TextContent = block.Content,
                ImageId = imageId,
                BlockData = JsonSerializer.Serialize(block, BlockDataJsonOptions),
                CreatedAt = DateTimeOffset.UtcNow,
                SubType = Truncate(block.Subtype, 50),
                TextLevel = ExtractTextLevel(block.Type, block.Subtype),
                TextFormat = Truncate(block.ContentFormat, MaxTextLength) ?? string.Empty,
                // StructaDoc bounding boxes are normalized 0-1; local convention is 0-1000.
                BboxX0 = Scale(block.BoundingBox?.X0),
                BboxY0 = Scale(block.BoundingBox?.Y0),
                BboxX1 = Scale(block.BoundingBox?.X1),
                BboxY1 = Scale(block.BoundingBox?.Y1),
                MineruScore = block.Confidence,
                Caption = block.Caption,
                Footnote = block.Footnote,
            });
        }

        return models;
    }

    private static float? Scale(double? value) =>
        value.HasValue ? (float)(value.Value * 1000f) : null;

    internal static int ExtractTextLevel(string? type, string? subtype)
    {
        // Read the full upstream subtype before its stored 50-character truncation.
        if (!string.IsNullOrEmpty(subtype)
            && subtype.StartsWith("heading-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(subtype.AsSpan(8), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
            && level > 0)
        {
            return level;
        }

        return type == "title" ? -2 : -1;
    }

    private static string? Truncate(string? value, int maxLength) =>
        value != null && value.Length > maxLength ? value[..maxLength] : value;
}
