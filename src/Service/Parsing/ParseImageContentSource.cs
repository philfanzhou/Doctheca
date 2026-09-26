using Microsoft.Extensions.Logging;
using Doctheca.Common.Oss;
using Doctheca.Domain.Models;
using Doctheca.Service.StructaDoc;

namespace Doctheca.Service.Parsing;

/// <summary>
/// Resolves parse image bytes from the owning storage: legacy parses read from OSS,
/// StructaDoc-backed parses stream the asset content from StructaDoc (ADR-0009).
/// </summary>
public sealed class ParseImageContentSource
{
    private readonly IStructaDocClient _structaDocClient;
    private readonly IOssService _ossService;
    private readonly ILogger<ParseImageContentSource> _logger;

    public ParseImageContentSource(
        IStructaDocClient structaDocClient,
        IOssService ossService,
        ILogger<ParseImageContentSource> logger)
    {
        _structaDocClient = structaDocClient;
        _ossService = ossService;
        _logger = logger;
    }

    /// <summary>
    /// Open the image byte stream. Returns null when the reference is broken;
    /// failures are logged and surfaced as exceptions to the caller.
    /// </summary>
    public async Task<Stream?> OpenAsync(DocumentParseModel parse, DocumentParseImageModel image, CancellationToken ct = default)
    {
        if (parse.StructaDocParseRunId is Guid runId)
        {
            if (!Guid.TryParse(image.ImagePath, out var assetId))
            {
                _logger.LogWarning(
                    "Image {ImageId} of parse {ParseId} does not reference a valid StructaDoc asset: {ImagePath}",
                    image.Id, parse.Id, image.ImagePath);
                return null;
            }

            return await _structaDocClient.GetAssetContentAsync(runId, assetId, ct);
        }

        return await _ossService.DownloadAsync(image.ImagePath);
    }
}
