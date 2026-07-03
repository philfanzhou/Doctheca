using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Domain.Services;

namespace Ruoyu.Study.DocLibrary.Service;

public static class DocumentParseEndpoints
{
    public static WebApplication MapDocumentParseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/document-parses")
            .RequireAuthorization();

        group.MapGet("/", ListDocumentParses);
        group.MapDelete("/{parseId:guid}", DeleteDocumentParse);

        return app;
    }

    private static async Task<IResult> ListDocumentParses(
        IDocumentParseService parseService,
        IDocumentFileService fileService,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null)
    {
        var (items, totalCount) = await parseService.GetListAsync(page, pageSize, search);

        var data = new List<object>();
        foreach (var p in items)
        {
            var file = await fileService.GetByIdAsync(p.DocumentFileId);
            data.Add(new
            {
                id = p.Id.ToString(),
                fileName = file?.FileName ?? "Unknown",
                status = p.Status,
                parsedAt = p.ParsedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                errorMessage = p.ErrorMessage,
            });
        }

        return Results.Ok(new
        {
            success = true,
            data,
            total = totalCount,
            page,
            pageSize,
            totalPages = (totalCount + pageSize - 1) / pageSize
        });
    }

    private static async Task<IResult> DeleteDocumentParse(
        Guid parseId,
        IDocumentParseService parseService,
        IDocumentFileService fileService,
        IOssService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentParseEndpoints));

        var parse = await parseService.GetByIdAsync(parseId);
        if (parse == null)
            return Results.NotFound(new { success = false, message = "Parse record not found", errorCode = "DOCLIBRARY_PARSE_NOT_FOUND" });

        // Delete associated images from S3
        var images = await parseService.GetImagesByParseIdAsync(parseId);
        foreach (var img in images)
        {
            try
            {
                await ossService.DeleteAsync(img.ImagePath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete image from OSS: {ImagePath}", img.ImagePath);
            }
        }

        // Delete parse record (cascade deletes images from DB)
        await parseService.DeleteParseAsync(parseId);

        return Results.Ok(new
        {
            success = true,
            data = new { id = parseId.ToString(), deleted = true }
        });
    }
}
