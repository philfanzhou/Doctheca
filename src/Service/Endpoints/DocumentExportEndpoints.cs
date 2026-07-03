using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Services;

namespace Ruoyu.Study.DocLibrary.Service;

public static class DocumentExportEndpoints
{
    public static WebApplication MapDocumentExportEndpoints(this WebApplication app)
    {
        var fileGroup = app.MapGroup("/admin/document-files")
            .RequireAuthorization();

        fileGroup.MapGet("/{id:guid}/export/markdown", ExportMarkdown);
        fileGroup.MapGet("/{id:guid}/export/html", ExportHtml);

        var parseGroup = app.MapGroup("/admin/document-parses")
            .RequireAuthorization();

        parseGroup.MapGet("/{parseId:guid}/export/markdown", ExportParseMarkdown);
        parseGroup.MapGet("/{parseId:guid}/export/html", ExportParseHtml);

        return app;
    }

    private static async Task<IResult> ExportMarkdown(
        Guid id,
        IDocumentFileService fileService,
        IDocumentParseService parseService,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentExportEndpoints));

        var file = await fileService.GetByIdAsync(id);
        if (file == null)
            return Results.NotFound(new { success = false, message = "File not found", errorCode = "DOCLIBRARY_FILE_NOT_FOUND" });

        var parse = await parseService.GetLatestByFileIdAsync(id);
        if (parse == null || parse.Status != DocumentParseStatus.Parsed)
            return Results.Json(new { success = false, message = "File is not parsed yet", errorCode = "DOCLIBRARY_FILE_NOT_PARSED" }, statusCode: StatusCodes.Status422UnprocessableEntity);

        return await ExportMarkdownCore(file.FileName, parse, parseService, ossService, logger);
    }

    private static async Task<IResult> ExportHtml(
        Guid id,
        IDocumentFileService fileService,
        IDocumentParseService parseService,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentExportEndpoints));

        var file = await fileService.GetByIdAsync(id);
        if (file == null)
            return Results.NotFound(new { success = false, message = "File not found", errorCode = "DOCLIBRARY_FILE_NOT_FOUND" });

        var parse = await parseService.GetLatestByFileIdAsync(id);
        if (parse == null || parse.Status != DocumentParseStatus.Parsed)
            return Results.Json(new { success = false, message = "File is not parsed yet", errorCode = "DOCLIBRARY_FILE_NOT_PARSED" }, statusCode: StatusCodes.Status422UnprocessableEntity);

        return await ExportHtmlCore(file.FileName, parse, parseService, ossService, logger);
    }

    private static async Task<IResult> ExportParseMarkdown(
        Guid parseId,
        IDocumentParseService parseService,
        IDocumentFileService fileService,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentExportEndpoints));

        var parse = await parseService.GetByIdAsync(parseId);
        if (parse == null)
            return Results.NotFound(new { success = false, message = "Parse record not found", errorCode = "DOCLIBRARY_PARSE_NOT_FOUND" });

        if (parse.Status != DocumentParseStatus.Parsed)
            return Results.Json(new { success = false, message = "Parse record is not parsed yet", errorCode = "DOCLIBRARY_PARSE_NOT_PARSED" }, statusCode: StatusCodes.Status422UnprocessableEntity);

        var file = await fileService.GetByIdAsync(parse.DocumentFileId);
        var fileName = file?.FileName ?? "document";

        return await ExportMarkdownCore(fileName, parse, parseService, ossService, logger);
    }

    private static async Task<IResult> ExportParseHtml(
        Guid parseId,
        IDocumentParseService parseService,
        IDocumentFileService fileService,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentExportEndpoints));

        var parse = await parseService.GetByIdAsync(parseId);
        if (parse == null)
            return Results.NotFound(new { success = false, message = "Parse record not found", errorCode = "DOCLIBRARY_PARSE_NOT_FOUND" });

        if (parse.Status != DocumentParseStatus.Parsed)
            return Results.Json(new { success = false, message = "Parse record is not parsed yet", errorCode = "DOCLIBRARY_PARSE_NOT_PARSED" }, statusCode: StatusCodes.Status422UnprocessableEntity);

        var file = await fileService.GetByIdAsync(parse.DocumentFileId);
        var fileName = file?.FileName ?? "document";

        return await ExportHtmlCore(fileName, parse, parseService, ossService, logger);
    }

    // ── Shared core logic ──

    private static async Task<IResult> ExportMarkdownCore(
        string fileName,
        DocumentParseModel parse,
        IDocumentParseService parseService,
        IOssService ossService,
        ILogger logger)
    {
        var images = await parseService.GetImagesByParseIdAsync(parse.Id);
        var markdownContent = MarkdownExportHelper.ReplaceImagePathsRelative(
            parse.MarkdownContent ?? string.Empty, images);

        var zipStream = await MarkdownExportHelper.BuildMarkdownZipAsync(
            fileName, markdownContent, images, ossService, logger);

        var zipFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_markdown.zip";
        return Results.Stream(zipStream, "application/zip", zipFileName);
    }

    private static async Task<IResult> ExportHtmlCore(
        string fileName,
        DocumentParseModel parse,
        IDocumentParseService parseService,
        IOssService ossService,
        ILogger logger)
    {
        var images = await parseService.GetImagesByParseIdAsync(parse.Id);
        var markdownContent = await MarkdownExportHelper.ReplaceImagePathsBase64Async(
            parse.MarkdownContent ?? string.Empty, images, ossService, logger);

        var htmlStream = MarkdownExportHelper.BuildHtmlStream(fileName, markdownContent);
        var htmlFileName = $"{Path.GetFileNameWithoutExtension(fileName)}.html";
        return Results.Stream(htmlStream, "text/html", htmlFileName);
    }
}
