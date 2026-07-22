using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Services;

namespace Ruoyu.Study.DocLibrary.Service.Parsing;

/// <summary>
/// Persists MinerU parse results: ZIP backup, images, blocks, and parse status updates.
/// </summary>
public sealed class MinerUResultPersistence
{
    private readonly IDocumentParseService _parseService;
    private readonly IDocumentParseBlockService _blockService;
    private readonly IOssService _ossService;
    private readonly ILogger<MinerUResultPersistence> _logger;

    public MinerUResultPersistence(
        IDocumentParseService parseService,
        IDocumentParseBlockService blockService,
        IOssService ossService,
        ILogger<MinerUResultPersistence> logger)
    {
        _parseService = parseService;
        _blockService = blockService;
        _ossService = ossService;
        _logger = logger;
    }

    /// <summary>
    /// Persist the full MinerU parse result: upload ZIP to OSS,
    /// insert images, parse and insert blocks, then mark the parse as Parsed.
    /// </summary>
    internal async Task PersistParseResultAsync(
        DocumentParseModel parse,
        DocumentFileModel file,
        MinerUParseResult result,
        CancellationToken ct)
    {
        // 1. Upload the full ZIP to OSS (raw data backup)
        string? zipPath = null;
        try
        {
            zipPath = await _ossService.UploadAsync(
                result.ZipBytes,
                "mineru-output.zip",
                "application/zip",
                OssBucket.Documents,
                $"mineru/{parse.DocumentFileId}");
            _logger.LogInformation("Full ZIP uploaded: {Path} ({Size} bytes)", zipPath, result.ZipBytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload full ZIP for parse {ParseId}", parse.Id);
        }

        // 2. Insert images and build the name -> id map for block referencing
        var imageNameToId = new Dictionary<string, Guid>();
        foreach (var img in result.Images)
        {
            var imageModel = new DocumentParseImageModel
            {
                ParseId = parse.Id,
                ImageName = img.ImageName,
                ImagePath = img.S3Path,
                ContentType = img.ContentType,
            };
            await _parseService.AddImageAsync(imageModel);
            imageNameToId[img.ImageName] = imageModel.Id;
        }
        _logger.LogInformation("Inserted {ImageCount} images for parse {ParseId}", result.Images.Count, parse.Id);

        // 3. Parse content_list.json and insert blocks
        if (!string.IsNullOrWhiteSpace(result.ContentListJson) && result.ContentListJson != "[]")
        {
            await _blockService.InsertBlocksFromContentListAsync(parse.Id, result.ContentListJson, imageNameToId);
        }

        // 4. Update parse with markdown, content_list, and new JSON fields
        await _parseService.UpdateStatusAsync(
            parse.Id,
            DocumentParseStatus.Parsed,
            markdownContent: result.Markdown,
            contentList: result.ContentListJson,
            contentListV2: result.ContentListV2Json,
            modelJson: result.ModelJson,
            layoutJson: result.LayoutJson,
            zipPath: zipPath);
        _logger.LogInformation("MinerU parse completed: ParseId={ParseId}, FileId={FileId}, " +
            "contentListV2={HasV2}, modelJson={HasModel}, layoutJson={HasLayout}",
            parse.Id, file.Id,
            result.ContentListV2Json != null ? "yes" : "no",
            result.ModelJson != null ? "yes" : "no",
            result.LayoutJson != null ? "yes" : "no");
    }

    /// <summary>
    /// Persist merged results from multiple chunks: combine markdown/content_list/blocks across chunks,
    /// prefix image names with chunk index to avoid collisions, and persist all artifacts.
    /// </summary>
    internal async Task PersistMergedChunkResultsAsync(
        DocumentParseModel parse,
        DocumentFileModel file,
        List<MinerUParseResult> chunkResults,
        string? errorMsg,
        string status,
        CancellationToken ct)
    {
        // Concatenate markdown with separator
        var allMarkdown = string.Join("\n\n---\n\n", chunkResults.Select(r => r.Markdown));

        // Concatenate content_list JSON arrays
        var mergedContentList = MergeContentListArrays(chunkResults);

        // Prefix image names with chunk index to avoid collisions across chunks
        var prefixedImages = new List<ImageMetadata>();
        for (int i = 0; i < chunkResults.Count; i++)
        {
            foreach (var img in chunkResults[i].Images)
            {
                var prefixedName = $"chunk{i}_{img.ImageName}";
                prefixedImages.Add(img with { ImageName = prefixedName });
            }
        }

        // Upload first chunk's ZIP as the canonical "mineru-output.zip" (covers all pages)
        var firstZip = chunkResults[0].ZipBytes;
        string? zipPath = null;
        try
        {
            zipPath = await _ossService.UploadAsync(
                firstZip,
                "mineru-output.zip",
                "application/zip",
                OssBucket.Documents,
                $"mineru/{parse.DocumentFileId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload ZIP for split parse {ParseId}", parse.Id);
        }

        // Use first chunk's layout.json and model.json if available
        var firstLayoutJson = chunkResults.FirstOrDefault(r => r.LayoutJson != null)?.LayoutJson;
        var firstModelJson = chunkResults.FirstOrDefault(r => r.ModelJson != null)?.ModelJson;
        var firstContentListV2 = chunkResults.FirstOrDefault(r => r.ContentListV2Json != null)?.ContentListV2Json;

        // Insert images (with prefixed names so they map to prefixed blocks)
        var imageNameToId = new Dictionary<string, Guid>();
        foreach (var img in prefixedImages)
        {
            var imageModel = new DocumentParseImageModel
            {
                ParseId = parse.Id,
                ImageName = img.ImageName,
                ImagePath = img.S3Path,
                ContentType = img.ContentType,
            };
            await _parseService.AddImageAsync(imageModel);
            imageNameToId[img.ImageName] = imageModel.Id;
        }

        // Insert blocks (block service handles overwriting)
        if (!string.IsNullOrEmpty(mergedContentList) && mergedContentList != "[]")
        {
            await _blockService.InsertBlocksFromContentListAsync(parse.Id, mergedContentList, imageNameToId);
        }

        await _parseService.UpdateStatusAsync(
            parse.Id,
            status,
            errorMessage: errorMsg,
            markdownContent: allMarkdown,
            contentList: mergedContentList,
            contentListV2: firstContentListV2,
            modelJson: firstModelJson,
            layoutJson: firstLayoutJson,
            zipPath: zipPath);
    }

    /// <summary>
    /// Merge multiple content_list.json arrays (one per chunk) into a single JSON array.
    /// Block page_id is preserved; sort_index is recomputed in DocumentParseBlockService.
    /// </summary>
    internal static string MergeContentListArrays(List<MinerUParseResult> chunkResults)
    {
        var allBlocks = new List<JsonElement>();
        foreach (var chunk in chunkResults)
        {
            if (string.IsNullOrWhiteSpace(chunk.ContentListJson) || chunk.ContentListJson == "[]") continue;
            try
            {
                using var doc = JsonDocument.Parse(chunk.ContentListJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        allBlocks.Add(el.Clone());
                    }
                }
            }
            catch
            {
                // Skip malformed chunk's content_list
            }
        }
        return JsonSerializer.Serialize(allBlocks);
    }
}
