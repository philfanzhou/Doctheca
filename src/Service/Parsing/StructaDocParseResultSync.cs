using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Services;
using Ruoyu.Study.DocLibrary.Service.StructaDoc;

namespace Ruoyu.Study.DocLibrary.Service.Parsing;

public interface IStructaDocParseResultSync
{
    /// <summary>
    /// Pull the full result of a succeeded Parse Run into local storage and mark the parse as parsed.
    /// Returns the canonical Markdown content.
    /// </summary>
    Task<string> SyncAsync(DocumentParseModel parse, Guid parseRunId, CancellationToken ct);
}

/// <summary>
/// Syncs a succeeded StructaDoc Parse Run into DocLibrary local tables (ADR-0009):
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
    private readonly IDocumentParseBlockRepository _blockRepository;
    private readonly IDocumentParseImageRepository _imageRepository;
    private readonly ILogger<StructaDocParseResultSync> _logger;

    public StructaDocParseResultSync(
        IStructaDocClient client,
        IDocumentParseService parseService,
        IDocumentParseBlockRepository blockRepository,
        IDocumentParseImageRepository imageRepository,
        ILogger<StructaDocParseResultSync> logger)
    {
        _client = client;
        _parseService = parseService;
        _blockRepository = blockRepository;
        _imageRepository = imageRepository;
        _logger = logger;
    }

    public async Task<string> SyncAsync(DocumentParseModel parse, Guid parseRunId, CancellationToken ct)
    {
        var assets = await _client.GetAssetsAsync(parseRunId, ct);
        var blocks = await _client.GetAllBlocksAsync(parseRunId, ct);
        var markdown = await _client.GetMarkdownAsync(parseRunId, ct);

        // Replace previous sync artifacts so a retried sync does not duplicate rows.
        await _imageRepository.DeleteByParseIdAsync(parse.Id);

        var assetIdToImageId = new Dictionary<Guid, Guid>(assets.Count);
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
            await _parseService.AddImageAsync(image);
            assetIdToImageId[asset.Id] = image.Id;
        }

        var blockModels = MapBlocks(parse.Id, blocks, assetIdToImageId);
        await _blockRepository.AddBlocksAsync(parse.Id, blockModels);

        await _parseService.UpdateStatusAsync(
            parse.Id,
            DocumentParseStatus.Parsed,
            markdownContent: markdown,
            structaDocParseRunId: parseRunId);

        _logger.LogInformation(
            "Synced StructaDoc parse run {ParseRunId} into parse {ParseId}: {BlockCount} blocks, {AssetCount} assets",
            parseRunId, parse.Id, blockModels.Count, assets.Count);

        return markdown;
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
                BlockType = Truncate(block.Type, MaxTextLength) ?? "unknown",
                TextContent = block.Content,
                ImageId = imageId,
                BlockData = JsonSerializer.Serialize(block, BlockDataJsonOptions),
                CreatedAt = DateTimeOffset.UtcNow,
                SubType = Truncate(block.Subtype, 50),
                TextLevel = ExtractTextLevel(block.Subtype),
                TextFormat = Truncate(block.ContentFormat, MaxTextLength) ?? string.Empty,
                // StructaDoc bounding boxes are normalized 0-1; local convention is 0-1000.
                BboxX0 = Scale(block.BoundingBox?.X0),
                BboxY0 = Scale(block.BoundingBox?.Y0),
                BboxX1 = Scale(block.BoundingBox?.X1),
                BboxY1 = Scale(block.BoundingBox?.Y1),
                MineruScore = block.Confidence,
                Caption = null,
            });
        }

        return models;
    }

    private static float? Scale(double? value) =>
        value.HasValue ? (float)(value.Value * 1000f) : null;

    private static int ExtractTextLevel(string? subtype)
    {
        // StructaDoc heading blocks carry subtype "heading-N"; map to the legacy text_level convention.
        if (!string.IsNullOrEmpty(subtype)
            && subtype.StartsWith("heading-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(subtype.AsSpan(8), out var level))
        {
            return level;
        }

        return -1;
    }

    private static string? Truncate(string? value, int maxLength) =>
        value != null && value.Length > maxLength ? value[..maxLength] : value;
}
