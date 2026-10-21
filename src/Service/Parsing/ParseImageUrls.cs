using Microsoft.Extensions.Logging;
using Doctheca.Common.Oss;
using Doctheca.Domain.Models;

namespace Doctheca.Service.Parsing;

internal static class ParseImageUrls
{
    /// <summary>
    /// Legacy parses resolve images to presigned OSS URLs; StructaDoc-backed parses
    /// resolve to the Doctheca image proxy endpoint (ADR-0009).
    /// </summary>
    internal static Func<DocumentParseImageModel, Task<string?>> BuildResolver(
        DocumentParseModel parse,
        IOssService ossService,
        ILogger logger)
    {
        if (parse.StructaDocParseRunId != null)
        {
            return img => Task.FromResult<string?>(ProxyUrl(parse.Id, img.Id));
        }

        return async img =>
        {
            try
            {
                return await ossService.GetPresignedUrlAsync(img.ImagePath, 3600);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to generate presigned URL for image: {ImagePath}", img.ImagePath);
                return null;
            }
        };
    }

    internal static string ProxyUrl(Guid parseId, Guid imageId) =>
        $"/admin/document-parses/{parseId:D}/images/{imageId:D}/content";
}
