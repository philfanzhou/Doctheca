using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocLibrary.Domain.Services;

namespace Ruoyu.Study.DocLibrary.Service;

/// <summary>
/// HTTP endpoints for QuestionBank pull-mode integration.
/// Exposes MinerU parsed data (importable list, structured blocks, images)
/// and accepts import status write-back to prevent duplicate processing.
/// </summary>
public static class QuestionBankImportEndpoints
{
    public static WebApplication MapQuestionBankImportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/document-parses");

        group.MapGet("/importable", ListImportableParses);
        group.MapGet("/{parseId:guid}/blocks", GetParseBlocks);
        group.MapGet("/images/{imageId:guid}", GetImage);
        group.MapPost("/{parseId:guid}/import-status", UpsertImportStatus);

        return app;
    }

    private static async Task<IResult> ListImportableParses(
        IQuestionBankImportService importService,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null,
        [FromQuery] bool includeImported = false)
    {
        var (items, totalCount) = await importService.GetImportableListAsync(page, pageSize, search, includeImported);

        var data = items.Select(x => new
        {
            parseId = x.ParseId.ToString(),
            fileId = x.FileId.ToString(),
            fileName = x.FileName,
            modelVersion = x.ModelVersion,
            parsedAt = x.ParsedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            importStatus = x.ImportStatus,
            importedAt = x.ImportedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
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

    private static async Task<IResult> GetParseBlocks(
        Guid parseId,
        IQuestionBankImportService importService,
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

    private static async Task<IResult> GetImage(
        Guid imageId,
        IQuestionBankImportService importService,
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

    private static async Task<IResult> UpsertImportStatus(
        Guid parseId,
        IQuestionBankImportService importService,
        [FromBody] ImportStatusRequest body,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(QuestionBankImportEndpoints));

        // Validate status
        if (body.Status != "imported" && body.Status != "failed")
            return Results.BadRequest(new { success = false, message = "Status must be 'imported' or 'failed'", errorCode = "DOCLIBRARY_IMPORT_STATUS_INVALID" });

        // Validate importedBy
        if (body.ImportedBy == Guid.Empty)
            return Results.BadRequest(new { success = false, message = "importedBy is required and must be a valid UUID", errorCode = "DOCLIBRARY_IMPORT_STATUS_INVALID" });

        // Serialize importedQuestionIds if provided
        string? importedQuestionIdsJson = null;
        if (body.ImportedQuestionIds != null && body.ImportedQuestionIds.Count > 0)
        {
            importedQuestionIdsJson = JsonSerializer.Serialize(body.ImportedQuestionIds);
        }

        ImportStatusResult result;
        try
        {
            result = await importService.UpsertImportStatusAsync(parseId, body.ImportedBy, body.Status, body.Note, importedQuestionIdsJson);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { success = false, message = "Parse record not found", errorCode = "DOCLIBRARY_PARSE_NOT_FOUND" });
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { success = false, message = "Status must be 'imported' or 'failed'", errorCode = "DOCLIBRARY_IMPORT_STATUS_INVALID" });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { success = false, message = ex.Message, errorCode = "DOCLIBRARY_PARSE_ALREADY_IMPORTED" },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        return Results.Ok(new
        {
            success = true,
            data = new
            {
                parseId = result.ParseId.ToString(),
                importStatus = result.ImportStatus,
                importedAt = result.ImportedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                updatedAt = result.UpdatedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            }
        });
    }
}

/// <summary>
/// Request body for POST /admin/document-parses/{parseId}/import-status
/// </summary>
public record ImportStatusRequest(
    Guid ImportedBy,
    string Status,
    string? Note,
    List<string>? ImportedQuestionIds);
