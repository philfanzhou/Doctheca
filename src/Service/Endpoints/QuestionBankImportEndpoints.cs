using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Services;

namespace Ruoyu.Study.DocLibrary.Service;

/// <summary>
/// HTTP endpoints for QuestionBank pull-mode integration.
/// Exposes MinerU parsed data as a read-only internal integration.
/// </summary>
public static class QuestionBankImportEndpoints
{
    public static WebApplication MapQuestionBankImportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/question-bank")
            .RequireAuthorization(DocLibraryAuthorizationPolicies.QuestionBank);

        group.MapGet("/document-parses", ListDocumentParsesAsync);
        group.MapGet("/document-parses/{parseId:guid}/blocks", GetParseBlocksAsync);
        group.MapGet("/images/{imageId:guid}", GetImageAsync);

        return app;
    }

    private static async Task<IResult> ListDocumentParsesAsync(
        [FromServices] IQuestionBankImportService importService,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null)
    {
        var (items, totalCount) = await importService.GetImportableListAsync(page, pageSize, search);

        var data = items.Select(x => new
        {
            parseId = x.ParseId.ToString(),
            fileId = x.FileId.ToString(),
            fileName = x.FileName,
            modelVersion = x.ModelVersion,
            parsedAt = x.ParsedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        }).ToList();

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

    private static async Task<IResult> GetParseBlocksAsync(
        Guid parseId,
        [FromServices] IQuestionBankImportService importService,
        [FromServices] ILoggerFactory loggerFactory,
        [FromQuery] int? pageId = null,
        [FromQuery] string? blockType = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var logger = loggerFactory.CreateLogger(nameof(QuestionBankImportEndpoints));

        List<ParseBlockItem> items;
        int totalCount;
        try
        {
            (items, totalCount) = await importService.GetBlocksAsync(parseId, pageId, blockType, page, pageSize);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { success = false, message = "Parse record not found", errorCode = "DOCLIBRARY_PARSE_NOT_FOUND" });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { success = false, message = ex.Message, errorCode = "DOCLIBRARY_PARSE_NOT_PARSED" },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var data = items.Select(b => new
        {
            id = b.Id.ToString(),
            parseId = b.ParseId.ToString(),
            pageId = b.PageId,
            sortIndex = b.SortIndex,
            blockType = b.BlockType,
            textContent = b.TextContent,
            imageName = b.ImageName,
            imagePath = b.ImagePath,
            imageUrl = b.ImageUrl,
            blockData = b.BlockData,
        }).ToList();

        return Results.Ok(new
        {
            success = true,
            data,
            total = totalCount,
            page,
            pageSize,
            totalPages = totalCount == 0 ? 1 : (totalCount + pageSize - 1) / pageSize
        });
    }

    private static async Task<IResult> GetImageAsync(
        Guid imageId,
        [FromServices] IQuestionBankImportService importService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(QuestionBankImportEndpoints));

        ParseImageBlob blob;
        try
        {
            blob = await importService.GetImageBlobAsync(imageId);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { success = false, message = "Image not found", errorCode = "DOCLIBRARY_IMAGE_NOT_FOUND" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download image {ImageId} from OSS", imageId);
            return Results.Json(new { success = false, message = "Failed to download image from OSS", errorCode = "DOCLIBRARY_OSS_DOWNLOAD_FAILED" },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Stream(blob.Stream, blob.ContentType, blob.ImageName);
    }
}
