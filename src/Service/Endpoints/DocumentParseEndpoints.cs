using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Doctheca.Common.Oss;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;
using Doctheca.Service.Parsing;
using Doctheca.Service.StructaDoc;

namespace Doctheca.Service;

public static class DocumentParseEndpoints
{
    public static WebApplication MapDocumentParseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/document-parses")
            .RequireAuthorization(DocthecaAuthorizationPolicies.Admin);

        // The two JSON endpoints carry the ServiceMantle security response-header baseline;
        // the image content endpoint below deliberately does not (binary rendering contract).
        group.MapGet("/", ListDocumentParses).RequireServiceMantleSecurityResponseHeaders();
        group.MapDelete("/{parseId:guid}", DeleteDocumentParse)
            .RequireServiceMantleSecurityResponseHeaders();
        group.MapGet("/{parseId:guid}/images/{imageId:guid}/content", GetParseImageContent);

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
                fileId = p.DocumentFileId.ToString(),
                fileName = file?.FileName ?? "Unknown",
                modelVersion = p.ModelVersion,
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
        IOssService ossService,
        ISearchIndexService searchIndexService,
        IStructaDocClient structaDocClient,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentParseEndpoints));

        var parse = await parseService.GetByIdAsync(parseId);
        if (parse == null)
            return Results.NotFound(new { success = false, message = "Parse record not found", errorCode = "DOCTHECA_PARSE_NOT_FOUND" });

        if (parse.StructaDocParseRunId is Guid runId)
        {
            // StructaDoc-backed parse: remote results are owned by StructaDoc (ADR-0009).
            // Best-effort cancel (in case the run is still active) then delete; local rows
            // and the search index are removed regardless of the remote outcome.
            try
            {
                await structaDocClient.CancelParseRunAsync(runId);
                await structaDocClient.DeleteParseRunAsync(runId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete StructaDoc parse run {ParseRunId}", runId);
            }
        }
        else
        {
            // Legacy parse: delete associated images from OSS (best-effort)
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
        }

        // Delete parse record (cascade deletes images and blocks from DB)
        await parseService.DeleteParseAsync(parseId);

        // Delete OpenSearch index for this parse (best-effort, does not block deletion)
        try
        {
            await searchIndexService.DeleteParseIndexAsync(parseId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete OpenSearch index for parse {ParseId}", parseId);
        }

        return Results.Ok(new
        {
            success = true,
            data = new { id = parseId.ToString(), deleted = true }
        });
    }

    /// <summary>
    /// Proxy image bytes of a StructaDoc-backed parse. Browsers authenticate with the
    /// admin cookie fallback, so the URL can be used directly in img tags.
    /// </summary>
    private static async Task<IResult> GetParseImageContent(
        Guid parseId,
        Guid imageId,
        IDocumentParseService parseService,
        ParseImageContentSource imageSource,
        CancellationToken cancellationToken)
    {
        var parse = await parseService.GetByIdAsync(parseId);
        if (parse == null)
            return Results.NotFound(new { success = false, message = "Parse record not found", errorCode = "DOCTHECA_PARSE_NOT_FOUND" });

        var images = await parseService.GetImagesByParseIdAsync(parseId);
        var image = images.Find(img => img.Id == imageId);
        if (image == null)
            return Results.NotFound(new { success = false, message = "Image not found", errorCode = "DOCTHECA_IMAGE_NOT_FOUND" });

        try
        {
            var stream = await imageSource.OpenAsync(parse, image, cancellationToken);
            if (stream == null)
                return Results.NotFound(new { success = false, message = "Image content not found", errorCode = "DOCTHECA_IMAGE_NOT_FOUND" });

            return Results.File(stream, image.ContentType, enableRangeProcessing: true);
        }
        catch (StructaDocException ex) when (ex.StatusCode == 404)
        {
            return Results.NotFound(new { success = false, message = "Image content not found in StructaDoc", errorCode = "DOCTHECA_IMAGE_NOT_FOUND" });
        }
        catch (StructaDocException ex)
        {
            return Results.Json(new { success = false, message = ex.Message, errorCode = "DOCTHECA_STRUCTADOC_ERROR" }, statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
