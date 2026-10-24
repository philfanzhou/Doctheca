using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Doctheca.Common.Oss;
using Doctheca.Domain.Models;
using Doctheca.Domain.Services;
using Doctheca.Service.Parsing;

namespace Doctheca.Service;

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
            return Results.NotFound(new { success = false, message = "File not found", errorCode = "DOCTHECA_FILE_NOT_FOUND" });

        var allParses = await parseService.GetByFileIdAsync(id);
        var parseResults = new List<object>();

        foreach (var parse in allParses)
        {
            var images = await parseService.GetImagesByParseIdAsync(parse.Id);
            var urlResolver = ParseImageUrls.BuildResolver(parse, ossService, logger);

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
                structureDefinitionId = file.StructureDefinitionId?.ToString(),
                subject = file.Subject, grade = file.Grade, year = file.Year,
                hasSource = file.HasSource, sourceState = file.SourceState,
                parses = parseResults,
            }
        });
    }
}
