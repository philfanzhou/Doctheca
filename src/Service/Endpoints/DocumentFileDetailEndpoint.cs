using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Oss;
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

            var markdownContent = parse.MarkdownContent;
            if (!string.IsNullOrEmpty(markdownContent))
            {
                markdownContent = await MarkdownExportHelper.ReplaceImagePathsPresignedAsync(
                    markdownContent, images, ossService, logger);
            }

            var imageList = new List<object>();
            foreach (var img in images)
            {
                try
                {
                    var presignedUrl = await ossService.GetPresignedUrlAsync(img.ImagePath, 3600);
                    imageList.Add(new { id = img.Id.ToString(), imageName = img.ImageName, imageUrl = presignedUrl });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to generate presigned URL for image: {ImagePath}", img.ImagePath);
                    imageList.Add(new { id = img.Id.ToString(), imageName = img.ImageName, imageUrl = img.ImagePath });
                }
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
}
