using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Services;

namespace Ruoyu.Study.DocLibrary.Service;

internal static class DocumentFileDetailEndpoint
{
    internal static RouteHandlerBuilder MapDetail(this RouteGroupBuilder group)
    {
        return group.MapGet("/{id:guid}", GetDocumentFile);
    }

    private static async Task<IResult> GetDocumentFile(
        Guid id,
        IDocumentFileService fileService,
        IDocumentParseService parseService,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentFileDetailEndpoint));

        var file = await fileService.GetByIdAsync(id);
        if (file == null)
            return Results.NotFound(new { success = false, message = "File not found", errorCode = "DOCLIBRARY_FILE_NOT_FOUND" });

        var allParses = await parseService.GetByFileIdAsync(id);
        var parseResults = new List<object>();

        foreach (var parse in allParses)
        {
            var images = await parseService.GetImagesByParseIdAsync(parse.Id);
            var urlResolver = BuildImageUrlResolver(parse, ossService, logger);

            var markdownContent = parse.MarkdownContent;
            if (!string.IsNullOrEmpty(markdownContent))
            {
                markdownContent = await MarkdownExportHelper.ReplaceImagePathsAsync(
                    markdownContent, images, urlResolver, logger);
            }

            var imageList = new List<object>();
            foreach (var img in images)
            {
                var imageUrl = await urlResolver(img) ?? img.ImagePath;
                imageList.Add(new { id = img.Id.ToString(), imageName = img.ImageName, imageUrl });
            }

            parseResults.Add(new
            {
                id = parse.Id.ToString(),
                modelVersion = parse.ModelVersion,
                status = parse.Status,
                markdownContent,
                contentList = parse.ContentList,
                contentListV2 = parse.ContentListV2,
                modelJson = parse.ModelJson,
                layoutJson = parse.LayoutJson,
                errorMessage = parse.ErrorMessage,
                parsedAt = parse.ParsedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                images = imageList,
            });
        }

        return Results.Ok(new
        {
            success = true,
            data = new
            {
                id = file.Id.ToString(),
                fileName = file.FileName,
                contentType = file.ContentType,
                createdAt = file.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                parses = parseResults,
            }
        });
    }

    /// <summary>
    /// Legacy parses resolve images to presigned OSS URLs; StructaDoc-backed parses
    /// resolve to the DocLibrary image proxy endpoint (ADR-0009).
    /// </summary>
    private static Func<DocumentParseImageModel, Task<string?>> BuildImageUrlResolver(
        DocumentParseModel parse,
        IOssService ossService,
        ILogger logger)
    {
        if (parse.StructaDocParseRunId != null)
        {
            return img => Task.FromResult<string?>(
                $"/admin/document-parses/{parse.Id:D}/images/{img.Id:D}/content");
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
}
